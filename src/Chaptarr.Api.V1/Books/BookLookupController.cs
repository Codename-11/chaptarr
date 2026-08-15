using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1;
using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Http;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Api.V1.Books
{
    [V1ApiController("book/lookup")]
    public class BookLookupController : Controller
    {
        private readonly ISearchForNewBook _searchProxy;
        private readonly IMapCoversToLocal _coverMapper;
        private readonly IMediaCoverProxy _mediaCoverProxy;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IProviderAliasService _providerAliasService;

        public BookLookupController(
            ISearchForNewBook searchProxy,
            IMapCoversToLocal coverMapper,
            IBookService bookService,
            IEditionService editionService,
            IMediaFileService mediaFileService,
            IMediaCoverProxy mediaCoverProxy,
            IProviderAliasService providerAliasService = null)
        {
            _searchProxy = searchProxy;
            _coverMapper = coverMapper;
            _bookService = bookService;
            _editionService = editionService;
            _mediaFileService = mediaFileService;
            _mediaCoverProxy = mediaCoverProxy;
            _providerAliasService = providerAliasService;
        }

        [HttpGet]
        [ProducesResponseType(typeof(List<BookResource>), 200)]
        [ProducesResponseType(typeof(ApiErrorResource), 400)]
        public ActionResult<List<BookResource>> Search([FromQuery] string term, [FromQuery] string mediaType = null)
        {
            if (string.IsNullOrWhiteSpace(term))
            {
                return BadRequest(new ApiErrorResource { Error = "term is required" });
            }

            var facadeContext = HttpContext.GetReadarrFacadeContext();
            if (facadeContext == null && ReadarrFacadeProviderIdTranslator.IsBareWorkTerm(term))
            {
                return BadRequest(new ApiErrorResource
                {
                    Error = ReadarrFacadeProviderIdTranslator.ProviderPrefixRequiredMessage("term")
                });
            }

            term = ReadarrFacadeProviderIdTranslator.NormalizeWorkTerm(term, facadeContext);

            var requestedMediaType = MediaTypeParameterParser.ParseOptional(mediaType);

            // 1) Try provider-prefixed ID local lookup first (e.g., hc:123, gr:456, ol:OL123, gb:xyz)
            var localMatches = LookupLocalByProviderOrEdition(term, requestedMediaType);

            // If a caller is explicitly requesting a mediaType, ignore local matches of the wrong type
            // and fall back to remote lookup instead.
            if (requestedMediaType.HasValue && localMatches != null)
            {
                localMatches = localMatches.Where(b => b.MediaType == requestedMediaType.Value).ToList();
                if (!localMatches.Any())
                {
                    localMatches = null;
                }
            }

            // 2) Fall back to remote search if nothing local. Text search results are lightweight
            // Goodreads hits and do not carry authoritative edition formats. Resolve each hit by
            // its canonical work id before applying mediaType so an ebook is not lost merely
            // because Book.MediaType defaults to Audiobook on the lightweight result.
            var searchResults = localMatches ?? SearchRemote(term, requestedMediaType);

            var resources = MapToResource(searchResults, facadeContext).ToList();
            BookResourceMapper.WarnFacadeIdentityGaps(resources, facadeContext, "book lookup response");
            return resources;
        }

        private List<NzbDrone.Core.Books.Book> SearchRemote(string term, BookMediaType? requestedMediaType)
        {
            var initialResults = _searchProxy.SearchForNewBook(term, null) ?? new List<NzbDrone.Core.Books.Book>();

            if (IsCanonicalProviderTerm(term))
            {
                return FilterByMediaTypeAndAvailability(initialResults, requestedMediaType);
            }

            var hydratedResults = new List<NzbDrone.Core.Books.Book>();
            var resolvedWorkIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var initialResult in initialResults.Where(book => book != null))
            {
                var canonicalWorkId = BookIdentity.GetStableWorkProviderIdentityTokens(initialResult)
                    .FirstOrDefault();

                // A lightweight result without a provider-owned work identity cannot be resolved
                // authoritatively. Do not guess its media type from the enum default.
                if (string.IsNullOrWhiteSpace(canonicalWorkId) || !resolvedWorkIds.Add(canonicalWorkId))
                {
                    continue;
                }

                var canonicalResults = _searchProxy.SearchForNewBook(canonicalWorkId, null) ?? new List<NzbDrone.Core.Books.Book>();
                hydratedResults.AddRange(FilterByMediaTypeAndAvailability(canonicalResults, requestedMediaType));
            }

            return hydratedResults
                .Where(book => book != null)
                .GroupBy(book => $"{BookIdentity.GetStableWorkProviderIdentityTokens(book).FirstOrDefault() ?? book.TitleSlug ?? book.Title}|{book.MediaType}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        private static List<NzbDrone.Core.Books.Book> FilterByMediaTypeAndAvailability(IEnumerable<NzbDrone.Core.Books.Book> books, BookMediaType? requestedMediaType)
        {
            return books?
                .Where(book => book != null &&
                               (!requestedMediaType.HasValue || book.MediaType == requestedMediaType.Value) &&
                               HasAuthoritativeMediaEdition(book))
                .ToList() ?? new List<NzbDrone.Core.Books.Book>();
        }

        private static bool HasAuthoritativeMediaEdition(NzbDrone.Core.Books.Book book)
        {
            return book?.Editions?.Any(edition =>
                edition != null &&
                (book.MediaType == BookMediaType.Ebook
                    ? edition.ReadingFormatId == 3 || edition.IsEbook
                    : edition.ReadingFormatId == 2 || edition.DurationSeconds.GetValueOrDefault() > 0)) == true;
        }

        private static bool IsCanonicalProviderTerm(string term)
        {
            if (string.IsNullOrWhiteSpace(term))
            {
                return false;
            }

            var separator = term.IndexOf(':');
            if (separator <= 0 || separator >= term.Length - 1 || term.IndexOf(':', separator + 1) >= 0)
            {
                return false;
            }

            return ProviderIdHelper.IsCanonicalPrefix(term.Substring(0, separator).Trim().ToLowerInvariant());
        }

        private List<NzbDrone.Core.Books.Book> LookupLocalByProviderOrEdition(string term, BookMediaType? requestedMediaType)
        {
            // Supports canonical prefixes: hc, gr, ol, gb, az, isbn.
            try
            {
                var idx = term.IndexOf(':');
                if (idx <= 0)
                {
                    return null;
                }

                var prefix = term.Substring(0, idx).Trim().ToLowerInvariant();
                var id = term.Substring(idx + 1).Trim();
                if (string.IsNullOrEmpty(id))
                {
                    return null;
                }

                string provider = prefix switch
                {
                    "hc" => "hc",
                    "gr" => "gr",
                    "ol" => "ol",
                    "gb" => "gb",
                    "az" => "az",
                    "isbn" => "isbn",
                    _ => null
                };

                if (provider != null && prefix == "hc" && !id.StartsWith("edition:", StringComparison.OrdinalIgnoreCase))
                {
                    var foundByWork = LookupLocalByProvider(provider, $"{prefix}:{id}", requestedMediaType);
                    if (foundByWork?.Any() == true)
                    {
                        return foundByWork;
                    }
                }

                if (prefix is "hc" or "gr" or "ol" or "gb" or "az" or "isbn")
                {
                    var localBooks = new List<NzbDrone.Core.Books.Book>();
                    var seenBookIds = new HashSet<int>();

                    foreach (var edition in _editionService.GetEditionsByProviderAndId(prefix, id) ?? new List<Edition>())
                    {
                        if (edition == null || edition.BookId <= 0 || !seenBookIds.Add(edition.BookId))
                        {
                            continue;
                        }

                        var book = _bookService.GetBook(edition.BookId);
                        if (book == null || (requestedMediaType.HasValue && book.MediaType != requestedMediaType.Value))
                        {
                            continue;
                        }

                        book.Editions = _editionService.GetEditionsByBook(book.Id);
                        book.BookFiles = _mediaFileService.GetFilesByBook(book.Id);
                        localBooks.Add(book);
                    }

                    if (localBooks.Any())
                    {
                        return localBooks.OrderBy(book => book.Id).ToList();
                    }
                }

                if (provider != null)
                {
                    var found = LookupLocalByProvider(provider, $"{prefix}:{id}", requestedMediaType);
                    if (found?.Any() == true)
                    {
                        return found;
                    }
                }
            }
            catch
            {
                // Be permissive: local lookup failures should fall back to remote search.
            }

            return null;
        }

        private List<NzbDrone.Core.Books.Book> LookupLocalByProvider(string provider, string providerId, BookMediaType? requestedMediaType)
        {
            if (requestedMediaType.HasValue)
            {
                var matches = _bookService.FindAllByProviderId(provider, providerId, requestedMediaType.Value) ?? new List<NzbDrone.Core.Books.Book>();
                foreach (var match in matches.Where(x => x != null))
                {
                    match.Editions = _editionService.GetEditionsByBook(match.Id);
                    match.BookFiles = _mediaFileService.GetFilesByBook(match.Id);
                }

                return matches.Where(x => x != null).ToList();
            }

            var unscopedMatches = new List<NzbDrone.Core.Books.Book>();
            var seenBookIds = new HashSet<int>();

            foreach (var mediaType in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
            {
                foreach (var match in _bookService.FindAllByProviderId(provider, providerId, mediaType) ?? new List<NzbDrone.Core.Books.Book>())
                {
                    if (match == null || !seenBookIds.Add(match.Id))
                    {
                        continue;
                    }

                    match.Editions = _editionService.GetEditionsByBook(match.Id);
                    match.BookFiles = _mediaFileService.GetFilesByBook(match.Id);
                    unscopedMatches.Add(match);
                }
            }

            return unscopedMatches.Any()
                ? unscopedMatches.OrderBy(book => book.Id).ToList()
                : null;
        }

        private IEnumerable<BookResource> MapToResource(IEnumerable<NzbDrone.Core.Books.Book> books, ReadarrFacadeContext facadeContext)
        {
            foreach (var currentBook in books)
            {
                var resource = currentBook.ToResource(new BookResourceMappingOptions { FacadeContext = facadeContext });

                resource.LocalAudiobookBooks = BookLocalInstanceLookup.FindExistingBooks(_bookService, _providerAliasService, currentBook, BookMediaType.Audiobook)
                    .Select(x => x.ToLocalInstanceResource())
                    .Where(x => x != null)
                    .ToList();
                resource.LocalEbookBooks = BookLocalInstanceLookup.FindExistingBooks(_bookService, _providerAliasService, currentBook, BookMediaType.Ebook)
                    .Select(x => x.ToLocalInstanceResource())
                    .Where(x => x != null)
                    .ToList();

                _coverMapper.ConvertToLocalUrls(resource.Id, MediaCoverEntity.Book, resource.Images);

                // Readarr/Seerr: lookup responses must include editions so clients can post them back on add.
                resource.Editions = currentBook.Editions?.ToResource(facadeContext) ?? new List<EditionResource>();
                foreach (var edition in resource.Editions)
                {
                    // Lookup/edition-choice rows need their own edition art. Mapping
                    // every edition through the book's one local cover collapses all
                    // choices to the monitored edition's image.
                    _mediaCoverProxy.ProxyRemoteUrls(edition.Images);
                }

                var monitoredEdition = currentBook.Editions?.FirstOrDefault(x => x.Monitored) ??
                                       currentBook.Editions?.FirstOrDefault();
                var cover = monitoredEdition?.Images?.FirstOrDefault(c => c.CoverType == MediaCoverTypes.Cover);

                if (cover != null)
                {
                    resource.RemoteCover = cover.Url;
                }

                yield return resource;
            }
        }
    }
}

﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Errors;
using ErsatzTV.Core.Interfaces.Images;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Search;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;
using Seq = LanguageExt.Seq;

namespace ErsatzTV.Core.Metadata
{
    public class MovieFolderScanner : LocalFolderScanner, IMovieFolderScanner
    {
        private readonly ILocalFileSystem _localFileSystem;
        private readonly ILocalMetadataProvider _localMetadataProvider;
        private readonly ILogger<MovieFolderScanner> _logger;
        private readonly IMovieRepository _movieRepository;
        private readonly ISearchIndex _searchIndex;

        public MovieFolderScanner(
            ILocalFileSystem localFileSystem,
            IMovieRepository movieRepository,
            ILocalStatisticsProvider localStatisticsProvider,
            ILocalMetadataProvider localMetadataProvider,
            IMetadataRepository metadataRepository,
            IImageCache imageCache,
            ISearchIndex searchIndex,
            ILogger<MovieFolderScanner> logger)
            : base(localFileSystem, localStatisticsProvider, metadataRepository, imageCache, logger)
        {
            _localFileSystem = localFileSystem;
            _movieRepository = movieRepository;
            _localMetadataProvider = localMetadataProvider;
            _searchIndex = searchIndex;
            _logger = logger;
        }

        public async Task<Either<BaseError, Unit>> ScanFolder(LibraryPath libraryPath, string ffprobePath)
        {
            if (!_localFileSystem.IsLibraryPathAccessible(libraryPath))
            {
                return new MediaSourceInaccessible();
            }

            var folderQueue = new Queue<string>();
            foreach (string folder in _localFileSystem.ListSubdirectories(libraryPath.Path).OrderBy(identity))
            {
                folderQueue.Enqueue(folder);
            }

            while (folderQueue.Count > 0)
            {
                string movieFolder = folderQueue.Dequeue();

                var allFiles = _localFileSystem.ListFiles(movieFolder)
                    .Filter(f => VideoFileExtensions.Contains(Path.GetExtension(f)))
                    .Filter(
                        f => !ExtraFiles.Any(
                            e => Path.GetFileNameWithoutExtension(f).EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                if (allFiles.Count == 0)
                {
                    foreach (string subdirectory in _localFileSystem.ListSubdirectories(movieFolder).OrderBy(identity))
                    {
                        folderQueue.Enqueue(subdirectory);
                    }

                    continue;
                }

                foreach (string file in allFiles.OrderBy(identity))
                {
                    // TODO: figure out how to rebuild playlists
                    Either<BaseError, MediaItemScanResult<Movie>> maybeMovie = await _movieRepository
                        .GetOrAdd(libraryPath, file)
                        .BindT(movie => UpdateStatistics(movie, ffprobePath))
                        .BindT(UpdateMetadata)
                        .BindT(movie => UpdateArtwork(movie, ArtworkKind.Poster))
                        .BindT(movie => UpdateArtwork(movie, ArtworkKind.FanArt));

                    await maybeMovie.Match(
                        async result =>
                        {
                            if (result.IsAdded)
                            {
                                await _searchIndex.AddItems(new List<MediaItem> { result.Item });
                            }
                            else if (result.IsUpdated)
                            {
                                await _searchIndex.UpdateItems(new List<MediaItem> { result.Item });
                            }
                        },
                        error =>
                        {
                            _logger.LogWarning("Error processing movie at {Path}: {Error}", file, error.Value);
                            return Task.CompletedTask;
                        });
                }
            }

            foreach (string path in await _movieRepository.FindMoviePaths(libraryPath))
            {
                if (!_localFileSystem.FileExists(path))
                {
                    _logger.LogInformation("Removing missing movie at {Path}", path);
                    List<int> ids = await _movieRepository.DeleteByPath(libraryPath, path);
                    await _searchIndex.RemoveItems(ids);
                }
            }

            return Unit.Default;
        }

        private async Task<Either<BaseError, MediaItemScanResult<Movie>>> UpdateMetadata(
            MediaItemScanResult<Movie> result)
        {
            try
            {
                Movie movie = result.Item;
                await LocateNfoFile(movie).Match(
                    async nfoFile =>
                    {
                        bool shouldUpdate = Optional(movie.MovieMetadata).Flatten().HeadOrNone().Match(
                            m => m.MetadataKind == MetadataKind.Fallback ||
                                 m.DateUpdated < _localFileSystem.GetLastWriteTime(nfoFile),
                            true);

                        if (shouldUpdate)
                        {
                            _logger.LogDebug("Refreshing {Attribute} from {Path}", "Sidecar Metadata", nfoFile);
                            if (await _localMetadataProvider.RefreshSidecarMetadata(movie, nfoFile))
                            {
                                result.IsUpdated = true;
                            }
                        }
                    },
                    async () =>
                    {
                        if (!Optional(movie.MovieMetadata).Flatten().Any())
                        {
                            string path = movie.MediaVersions.Head().MediaFiles.Head().Path;
                            _logger.LogDebug("Refreshing {Attribute} for {Path}", "Fallback Metadata", path);
                            if (await _localMetadataProvider.RefreshFallbackMetadata(movie))
                            {
                                result.IsUpdated = true;
                            }
                        }
                    });

                return result;
            }
            catch (Exception ex)
            {
                return BaseError.New(ex.ToString());
            }
        }

        private async Task<Either<BaseError, MediaItemScanResult<Movie>>> UpdateArtwork(
            MediaItemScanResult<Movie> result,
            ArtworkKind artworkKind)
        {
            try
            {
                Movie movie = result.Item;
                await LocateArtwork(movie, artworkKind).IfSomeAsync(
                    async posterFile =>
                    {
                        MovieMetadata metadata = movie.MovieMetadata.Head();
                        await RefreshArtwork(posterFile, metadata, artworkKind);
                    });

                return result;
            }
            catch (Exception ex)
            {
                return BaseError.New(ex.ToString());
            }
        }

        private Option<string> LocateNfoFile(Movie movie)
        {
            string path = movie.MediaVersions.Head().MediaFiles.Head().Path;
            string movieAsNfo = Path.ChangeExtension(path, "nfo");
            string movieNfo = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, "movie.nfo");
            return Seq.create(movieAsNfo, movieNfo)
                .Filter(s => _localFileSystem.FileExists(s))
                .HeadOrNone();
        }

        private Option<string> LocateArtwork(Movie movie, ArtworkKind artworkKind)
        {
            string segment = artworkKind switch
            {
                ArtworkKind.Poster => "poster",
                ArtworkKind.FanArt => "fanart",
                _ => throw new ArgumentOutOfRangeException(nameof(artworkKind))
            };

            string path = movie.MediaVersions.Head().MediaFiles.Head().Path;
            string folder = Path.GetDirectoryName(path) ?? string.Empty;
            IEnumerable<string> possibleMoviePosters = ImageFileExtensions.Collect(
                    ext => new[] { $"{segment}.{ext}", Path.GetFileNameWithoutExtension(path) + $"-{segment}.{ext}" })
                .Map(f => Path.Combine(folder, f));
            Option<string> result = possibleMoviePosters.Filter(p => _localFileSystem.FileExists(p)).HeadOrNone();
            if (result.IsNone && artworkKind == ArtworkKind.Poster)
            {
                IEnumerable<string> possibleFolderPosters = ImageFileExtensions.Collect(
                        ext => new[] { $"folder.{ext}" })
                    .Map(f => Path.Combine(folder, f));
                result = possibleFolderPosters.Filter(p => _localFileSystem.FileExists(p)).HeadOrNone();
            }

            return result;
        }
    }
}

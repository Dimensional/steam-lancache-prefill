﻿namespace SteamPrefill
{
    public sealed class SteamManager : IDisposable
    {
        private readonly IAnsiConsole _ansiConsole;
        private readonly DownloadArguments _downloadArgs;

        private readonly Steam3Session _steam3;
        private readonly CdnPool _cdnPool;

        private readonly DownloadHandler _downloadHandler;
        private readonly DepotHandler _depotHandler;
        private readonly AppInfoHandler _appInfoHandler;

        private readonly PrefillSummaryResult _prefillSummaryResult = new PrefillSummaryResult();

        public SteamManager(IAnsiConsole ansiConsole, DownloadArguments downloadArgs)
        {
            _ansiConsole = ansiConsole;
            _downloadArgs = downloadArgs;

            _steam3 = new Steam3Session(_ansiConsole);
            _cdnPool = new CdnPool(_ansiConsole, _steam3);
            _appInfoHandler = new AppInfoHandler(_ansiConsole, _steam3, _steam3.LicenseManager);
            _downloadHandler = new DownloadHandler(_ansiConsole, _cdnPool);
            _depotHandler = new DepotHandler(_ansiConsole, _steam3, _appInfoHandler, _cdnPool);
        }

        #region Startup + Shutdown

        /// <summary>
        /// Logs the user into the Steam network, and retrieves available CDN servers and account licenses.
        ///
        /// Required to be called first before using SteamManager class.
        /// </summary>
        public async Task InitializeAsync()
        {
            var timer = Stopwatch.StartNew();
            _ansiConsole.LogMarkupLine("Starting login!");

            await _steam3.LoginToSteamAsync();
            _steam3.WaitForLicenseCallback();

            _ansiConsole.LogMarkupLine("Steam session initialization complete!", timer);
            // White spacing + a horizontal rule to delineate that initialization has completed
            _ansiConsole.WriteLine();
            _ansiConsole.Write(new Rule());

        }

        public void Shutdown()
        {
            _steam3.Disconnect();
        }

        public void Dispose()
        {
            _downloadHandler.Dispose();
            _steam3.Dispose();
        }

        #endregion

        #region Prefill

        public async Task DownloadMultipleAppsAsync(bool downloadAllOwnedGames, bool prefillRecentGames)
        {
            // Building out full list of AppIds to use.
            var appIdsToDownload = LoadPreviouslySelectedApps();
            if (downloadAllOwnedGames)
            {
                appIdsToDownload.AddRange(_steam3.LicenseManager.AllOwnedAppIds);
            }
            if (prefillRecentGames)
            {
                var recentGames = await _appInfoHandler.GetRecentlyPlayedGamesAsync();
                appIdsToDownload.AddRange(recentGames.Select(e => (uint)e.appid));
            }

            // AppIds can potentially be added twice when building out the full list of ids
            var distinctAppIds = appIdsToDownload.Distinct().ToList();
            await _appInfoHandler.RetrieveAppMetadataAsync(distinctAppIds);

            // Whitespace divider
            _ansiConsole.WriteLine();

            var availableGames = await _appInfoHandler.GetAvailableGamesByIdAsync(distinctAppIds);
            //TODO switch this to iterating over the list of apps instead
            foreach (var app in availableGames)
            {
                try
                {
                    await DownloadSingleAppAsync(app.AppId);
                }
                catch (Exception e) when (e is LancacheNotFoundException || e is InfiniteLoopException)
                {
                    // We'll want to bomb out the entire process for these exceptions, as they mean we can't prefill any apps at all
                    throw;
                }
                catch (Exception e)
                {
                    // Need to catch any exceptions that might happen during a single download, so that the other apps won't be affected
                    _ansiConsole.LogMarkupLine(Red($"Unexpected download error : {e.Message}  Skipping app..."));
                    _ansiConsole.MarkupLine("");
                    FileLogger.LogException(e);

                    _prefillSummaryResult.FailedApps++;
                }
            }
            await PrintUnownedAppsAsync(distinctAppIds);

            _ansiConsole.LogMarkupLine("Prefill complete!");
            _prefillSummaryResult.RenderSummaryTable(_ansiConsole);
        }

        private async Task DownloadSingleAppAsync(uint appId)
        {
            AppInfo appInfo = await _appInfoHandler.GetAppInfoAsync(appId);

            var filteredDepots = await _depotHandler.FilterDepotsToDownloadAsync(appInfo.Depots);
            if (filteredDepots.Empty())
            {
                _ansiConsole.LogMarkupLine($"Starting {Cyan(appInfo)}  {LightYellow("No depots to download.  Current arguments filtered all depots")}");
                return;
            }

            await _depotHandler.BuildLinkedDepotInfoAsync(filteredDepots);

            // We will want to re-download the entire app, if any of the depots have been updated
            if (_downloadArgs.Force == false && _depotHandler.AppIsUpToDate(filteredDepots))
            {
                _prefillSummaryResult.AlreadyUpToDate++;
                return;
            }

            _ansiConsole.LogMarkupLine($"Starting {Cyan(appInfo)}");

            await _cdnPool.PopulateAvailableServersAsync();

            // Get the full file list for each depot, and queue up the required chunks
            List<QueuedRequest> chunkDownloadQueue = null;
            await _ansiConsole.StatusSpinner().StartAsync("Fetching depot manifests...", async _ => { chunkDownloadQueue = await _depotHandler.BuildChunkDownloadQueueAsync(filteredDepots); });

            // Finally run the queued downloads
            var downloadTimer = Stopwatch.StartNew();
            var totalBytes = ByteSize.FromBytes(chunkDownloadQueue.Sum(e => e.CompressedLength));
            _prefillSummaryResult.TotalBytesTransferred += totalBytes;

            _ansiConsole.LogMarkupVerbose($"Downloading {Magenta(totalBytes.ToDecimalString())} from {LightYellow(chunkDownloadQueue.Count)} chunks");

            if (AppConfig.SkipDownloads)
            {
                _ansiConsole.MarkupLine("");
                return;
            }

            var downloadSuccessful = await _downloadHandler.DownloadQueuedChunksAsync(chunkDownloadQueue, _downloadArgs);
            if (downloadSuccessful)
            {
                _depotHandler.MarkDownloadAsSuccessful(filteredDepots);
                _prefillSummaryResult.Updated++;

                // Logging some metrics about the download
                _ansiConsole.LogMarkupLine($"Finished in {LightYellow(downloadTimer.FormatElapsedString())} - {Magenta(totalBytes.CalculateBitrate(downloadTimer))}");
                _ansiConsole.WriteLine();
            }
            else
            {
                _prefillSummaryResult.FailedApps++;
            }
            downloadTimer.Stop();
        }

        #endregion

        #region Select Apps

        public void SetAppsAsSelected(List<TuiAppInfo> tuiAppModels)
        {
            List<uint> selectedAppIds = tuiAppModels.Where(e => e.IsSelected)
                                                    .Select(e => UInt32.Parse(e.AppId))
                                                    .ToList();
            File.WriteAllText(AppConfig.UserSelectedAppsPath, JsonSerializer.Serialize(selectedAppIds, SerializationContext.Default.ListUInt32));

            _ansiConsole.LogMarkupLine($"Selected {Magenta(selectedAppIds.Count)} apps to prefill!  ");
        }

        public List<uint> LoadPreviouslySelectedApps()
        {
            if (!File.Exists(AppConfig.UserSelectedAppsPath))
            {
                return new List<uint>();
            }

            return JsonSerializer.Deserialize(File.ReadAllText(AppConfig.UserSelectedAppsPath), SerializationContext.Default.ListUInt32);
        }

        #endregion

        public async Task<List<AppInfo>> GetAllAvailableAppsAsync()
        {
            var ownedGameIds = _steam3.LicenseManager.AllOwnedAppIds;

            // Loading app metadata from steam, skipping related DLC apps
            await _appInfoHandler.RetrieveAppMetadataAsync(ownedGameIds, loadDlcApps: false, getRecentlyPlayedMetadata: true);
            var availableGames = await _appInfoHandler.GetAvailableGamesByIdAsync(ownedGameIds);

            return availableGames;
        }

        private async Task PrintUnownedAppsAsync(List<uint> distinctAppIds)
        {
            // Write out any apps that can't be downloaded as a warning message, so users can know that they were skipped
            AppInfo[] unownedApps = await Task.WhenAll(distinctAppIds.Where(e => !_steam3.LicenseManager.AccountHasAppAccess(e))
                                                                     .Select(e => _appInfoHandler.GetAppInfoAsync(e)));
            _prefillSummaryResult.UnownedAppsSkipped = unownedApps.Length;


            if (unownedApps.Empty())
            {
                return;
            }

            var table = new Table { Border = TableBorder.MinimalHeavyHead };
            // Header
            table.AddColumn(new TableColumn(White("App")));

            // Rows
            foreach (var app in unownedApps.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                table.AddRow($"[link=https://store.steampowered.com/app/{app.AppId}]🔗[/] {White(app.Name)}");
            }

            _ansiConsole.MarkupLine("");
            _ansiConsole.MarkupLine(LightYellow($" Warning!  Found {Magenta(unownedApps.Length)} unowned apps!  They will be excluded from this prefill run..."));
            _ansiConsole.Write(table);
        }

    }
}
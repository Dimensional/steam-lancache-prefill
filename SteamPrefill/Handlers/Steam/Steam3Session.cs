namespace SteamPrefill.Handlers.Steam
{
    public sealed class Steam3Session : IDisposable
    {
        // Steam services
        private readonly SteamClient _steamClient;
        private readonly SteamUser _steamUser;
        public readonly SteamContent SteamContent;
        public readonly SteamApps SteamAppsApi;
        public readonly Client CdnClient;
        public SteamUnifiedMessages.UnifiedService<IPlayer> unifiedPlayerService;

        private readonly CallbackManager _callbackManager;

        private SteamUser.LogOnDetails _logonDetails;
        private readonly IAnsiConsole _ansiConsole;

        private readonly UserAccountStore _userAccountStore;

        public readonly LicenseManager LicenseManager;

        public SteamID _steamId;

        public Steam3Session(IAnsiConsole ansiConsole)
        {
            _ansiConsole = ansiConsole;

            _steamClient = new SteamClient();
            _steamUser = _steamClient.GetHandler<SteamUser>();
            SteamAppsApi = _steamClient.GetHandler<SteamApps>();
            SteamContent = _steamClient.GetHandler<SteamContent>();
            SteamUnifiedMessages steamUnifiedMessages = _steamClient.GetHandler<SteamUnifiedMessages>();
            unifiedPlayerService = steamUnifiedMessages.CreateService<IPlayer>();

            _callbackManager = new CallbackManager(_steamClient);

            // This callback is triggered when SteamKit2 makes a successful connection
            _callbackManager.Subscribe<SteamClient.ConnectedCallback>(e =>
            {
                _isConnecting = false;
                _disconnected = false;
            });
            // If a connection attempt fails in anyway, SteamKit2 notifies of the failure with a "disconnect"
            _callbackManager.Subscribe<SteamClient.DisconnectedCallback>(e =>
            {
                _isConnecting = false;
                _disconnected = true;
            });

            _callbackManager.Subscribe<SteamUser.LoggedOnCallback>(loggedOn => _loggedOnCallbackResult = loggedOn);
            _callbackManager.Subscribe<SteamUser.UpdateMachineAuthCallback>(UpdateMachineAuthCallback);
            _callbackManager.Subscribe<SteamUser.LoginKeyCallback>(LoginKeyCallback);
            _callbackManager.Subscribe<SteamApps.LicenseListCallback>(LicenseListCallback);

            CdnClient = new Client(_steamClient);
            // Configuring SteamKit's HttpClient to timeout in a more reasonable time frame.
            Client.ResponseBodyTimeout = TimeSpan.FromSeconds(5);
            Client.RequestTimeout = TimeSpan.FromSeconds(5);

            _userAccountStore = UserAccountStore.LoadFromFile();
            LicenseManager = new LicenseManager(SteamAppsApi, _userAccountStore);
        }

        public async Task LoginToSteamAsync()
        {
            await ConfigureLoginDetailsAsync();

            int retryCount = 0;
            bool logonSuccess = false;
            while (!logonSuccess)
            {
                _callbackManager.RunWaitAllCallbacks(timeout: TimeSpan.FromMilliseconds(50));
                SteamUser.LoggedOnCallback logonResult = null;
                _ansiConsole.StatusSpinner().Start("Connecting to Steam...", ctx =>
                {
                    ConnectToSteam();

                    ctx.Status = "Logging into Steam...";
                    logonResult = AttemptSteamLogin();
                });

                logonSuccess = HandleLogonResult(logonResult);

                retryCount++;
                if (retryCount == 5)
                {
                    throw new SteamLoginException("Unable to login to Steam!  Try again in a few moments...");
                }
            }

            _ansiConsole.StatusSpinner().Start("Saving Steam session...", _ =>
            {
                TryWaitForLoginKey();
            });
        }

        #region  Connecting to Steam

        // Used to busy wait until the connection attempt finishes in either a success or failure
        private bool _isConnecting;

        /// <summary>
        /// Attempts to establish a connection to the Steam network.
        /// Retries if necessary until successful connection is established
        /// </summary>
        /// <exception cref="SteamConnectionException">Throws if unable to connect to Steam</exception>
        private void ConnectToSteam()
        {
            var timeoutAfter = DateTime.Now.AddSeconds(30);

            // Busy waiting until the client has a successful connection established
            while (!_steamClient.IsConnected)
            {
                _isConnecting = true;
                _steamClient.Connect();

                // Busy waiting until SteamKit2 either succeeds/fails the connection attempt
                while (_isConnecting)
                {
                    _callbackManager.RunWaitAllCallbacks(timeout: TimeSpan.FromMilliseconds(50));
                    if (DateTime.Now > timeoutAfter)
                    {
                        throw new SteamConnectionException("Timeout connecting to Steam...  Try again in a few moments");
                    }
                }
            }
        }

        #endregion

        #region Logging into Steam

        private async Task ConfigureLoginDetailsAsync()
        {
            var username = await _userAccountStore.GetUsernameAsync(_ansiConsole);

            string loginKey;
            _userAccountStore.LoginKeys.TryGetValue(username, out loginKey);

            _logonDetails = new SteamUser.LogOnDetails
            {
                Username = username,
                Password = loginKey == null ? _ansiConsole.ReadPassword() : null,
                ShouldRememberPassword = true,
                LoginKey = loginKey,
                LoginID = 5995
            };
            // Sentry file is required when using Steam Guard w\ email
            if (_userAccountStore.SentryData.TryGetValue(_logonDetails.Username, out var bytes))
            {
                _logonDetails.SentryFileHash = bytes.ToSha1();
            }
        }

        private SteamUser.LoggedOnCallback _loggedOnCallbackResult;

        [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "while() loop is not infinite.  _loggedOnCallbackResult is set after logging into Steam")]
        private SteamUser.LoggedOnCallback AttemptSteamLogin()
        {
            var timeoutAfter = DateTime.Now.AddSeconds(30);

            _loggedOnCallbackResult = null;
            _steamUser.LogOn(_logonDetails);

            // Busy waiting for the callback to complete, then we can return the callback value synchronously
            while (_loggedOnCallbackResult == null)
            {
                _callbackManager.RunWaitAllCallbacks(timeout: TimeSpan.FromMilliseconds(50));
                if (DateTime.Now > timeoutAfter)
                {
                    throw new SteamLoginException("Timeout logging into Steam...  Try again in a few moments");
                }
            }
            return _loggedOnCallbackResult;
        }

        private int _failedLogonAttempts;
        private bool HandleLogonResult(SteamUser.LoggedOnCallback logonResult)
        {
            _steamId = logonResult.ClientSteamID;

            var loggedOn = logonResult;

            // If the account has 2-Factor login enabled, then we will need to re-login with the supplied code
            if (loggedOn.Result == EResult.AccountLoginDeniedNeedTwoFactor)
            {
                _logonDetails.TwoFactorCode = _ansiConsole.Prompt(new TextPrompt<string>(LightYellow("2FA required for login.") +
                                                                                         $"  Please enter your {Cyan("Steam Guard code")} from your authenticator app : "));
                return false;
            }
            if (loggedOn.Result == EResult.TwoFactorCodeMismatch)
            {
                _logonDetails.TwoFactorCode = _ansiConsole.Prompt(new TextPrompt<string>(Red("Login failed. Incorrect Steam Guard code") +
                                                                                         "  Please try again : "));
                return false;
            }

            var loginKeyExpired = _logonDetails.LoginKey != null && loggedOn.Result == EResult.InvalidPassword;
            if (loginKeyExpired)
            {
                _userAccountStore.LoginKeys.Remove(_logonDetails.Username);
                _logonDetails.LoginKey = null;
                _logonDetails.Password = _ansiConsole.ReadPassword("Steam session expired!  Password re-entry required!");
                return false;
            }

            if (loggedOn.Result == EResult.InvalidPassword)
            {
                _failedLogonAttempts++;
                if (_failedLogonAttempts == 3)
                {
                    _ansiConsole.LogMarkupLine(Red("Invalid username/password combination!  Check your login credential validity, and try again.."));
                    throw new AuthenticationException("Invalid username/password");
                }

                _logonDetails.Password = _ansiConsole.ReadPassword($"{Red("Invalid password!  Please re-enter your password!")}");
                return false;
            }

            // SteamGuard code required
            if (loggedOn.Result == EResult.AccountLogonDenied)
            {
                _logonDetails.AuthCode = _ansiConsole.Prompt(new TextPrompt<string>(LightYellow("This account is protected by Steam Guard.") +
                                                                                    "  Please enter the code sent to your email address:  "));
                return false;
            }
            if (loggedOn.Result == EResult.ServiceUnavailable)
            {
                throw new SteamLoginException($"Unable to login to Steam : Service is unavailable");
            }
            if (loggedOn.Result != EResult.OK)
            {
                throw new SteamLoginException($"Unable to login to Steam.  An unknown error occurred : {loggedOn.Result}");
            }

            _ansiConsole.LogMarkupLine($"Logged into Steam");

            // Forcing a garbage collect to remove stored password from memory
            if (_logonDetails.Password != null)
            {
                _logonDetails.Password = null;
                GC.Collect(3, GCCollectionMode.Forced);
            }

            return true;
        }

        bool _receivedLoginKey;
        private void TryWaitForLoginKey()
        {
            if (_logonDetails.LoginKey != null)
            {
                return;
            }

            var totalWaitPeriod = DateTime.Now.AddSeconds(10);
            while (!_receivedLoginKey)
            {
                if (DateTime.Now >= totalWaitPeriod)
                {
                    _ansiConsole.LogMarkupLine(Red("Failed to save Steam session key.  Steam account will not stay logged in..."));
                    return;
                }
                _callbackManager.RunWaitAllCallbacks(TimeSpan.FromMilliseconds(50));
            }
        }

        private void LoginKeyCallback(SteamUser.LoginKeyCallback loginKey)
        {
            _userAccountStore.LoginKeys[_logonDetails.Username] = loginKey.LoginKey;
            _userAccountStore.Save();

            _steamUser.AcceptNewLoginKey(loginKey);
            _receivedLoginKey = true;
        }

        private bool _disconnected = true;
        public void Disconnect()
        {
            if (_disconnected)
            {
                _ansiConsole.LogMarkupLine("Already disconnected from Steam");
                return;
            }

            _disconnected = false;
            _steamClient.Disconnect();

            _ansiConsole.StatusSpinner().Start("Disconnecting", context =>
            {
                while (!_disconnected)
                {
                    _callbackManager.RunWaitAllCallbacks(TimeSpan.FromMilliseconds(100));
                }
            });
            _ansiConsole.LogMarkupLine("Disconnected from Steam!");
        }

        #endregion

        #region Other Auth Methods

        /// <summary>
        /// The UpdateMachineAuth event will be triggered once the user has logged in with either Steam Guard or 2FA enabled.
        /// This callback handler will save a "sentry file" for future logins, that will allow an existing Steam session to be reused,
        /// without requiring a password.
        ///
        /// Despite the fact that this will be triggered for both Steam Guard + 2FA, the sentry file is only required for re-login when using an
        /// account with Steam Guard enabled.
        /// </summary>
        private void UpdateMachineAuthCallback(SteamUser.UpdateMachineAuthCallback machineAuth)
        {
            _userAccountStore.SentryData[_logonDetails.Username] = machineAuth.Data;
            _userAccountStore.Save();

            var authResponse = new SteamUser.MachineAuthDetails
            {
                BytesWritten = machineAuth.BytesToWrite,
                FileName = machineAuth.FileName,
                FileSize = machineAuth.BytesToWrite,
                Offset = machineAuth.Offset,
                // should be the sha1 hash of the sentry file we just received
                SentryFileHash = machineAuth.Data.ToSha1(),
                OneTimePassword = machineAuth.OneTimePassword,
                LastError = 0,
                Result = EResult.OK,
                JobID = machineAuth.JobID
            };
            _steamUser.SendMachineAuthResponse(authResponse);
        }

        #endregion

        #region LoadAccountLicenses

        private bool _loadAccountLicensesIsRunning = true;
        /// <summary>
        /// Waits for the user's currently owned licenses(games) to be returned.
        /// The license query is triggered on application startup, and requires busy-waiting to receive the callback
        /// </summary>
        public void WaitForLicenseCallback()
        {
            _ansiConsole.StatusSpinner().Start("Retrieving owned apps...", _ =>
            {
                while (_loadAccountLicensesIsRunning)
                {
                    _callbackManager.RunWaitAllCallbacks(timeout: TimeSpan.FromMilliseconds(50));
                }
            });
        }

        private void LicenseListCallback(SteamApps.LicenseListCallback licenseList)
        {
            _loadAccountLicensesIsRunning = false;
            if (licenseList.Result != EResult.OK)
            {
                _ansiConsole.MarkupLine(Red($"Unexpected error while retrieving license list : {licenseList.Result}"));
                throw new SteamLoginException("Unable to retrieve user licenses!");
            }
            LicenseManager.LoadPackageInfo(licenseList.LicenseList);
        }

        #endregion
        
        public void Dispose()
        {
            CdnClient.Dispose();
        }
    }
}
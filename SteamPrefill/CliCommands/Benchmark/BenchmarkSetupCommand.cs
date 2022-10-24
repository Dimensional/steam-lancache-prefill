﻿// ReSharper disable MemberCanBePrivate.Global - Properties used as parameters can't be private with CliFx, otherwise they won't work.
namespace SteamPrefill.CliCommands.Benchmark
{
    [UsedImplicitly]
    [Command("benchmark setup", Description = "Configures a benchmark workload from multiple apps.  Benchmark workload is static, and portable between machines.")]
    public class BenchmarkSetupCommand : ICommand
    {
        [CommandOption("appid", Description = "The id of one or more games to include in benchmark workload file.  AppIds can be found using https://steamdb.info/")]
        public List<uint> AppIds { get; init; } = new List<uint>();

        [CommandOption("all", Description = "Includes all currently owned games in benchmark workload file", Converter = typeof(NullableBoolConverter))]
        public bool? BenchmarkAllOwnedGames { get; init; }

        [CommandOption("use-selected", Description = "Includes games selected using 'select-apps' in the benchmark workload file", Converter = typeof(NullableBoolConverter))]
        public bool? UseSelectedApps { get; init; }

        [CommandOption("nocache",
            Description = "Skips using locally cached files.  Saves disk space, at the expense of slower subsequent runs.",
            Converter = typeof(NullableBoolConverter))]
        public bool? NoLocalCache { get; init; }

        private IAnsiConsole _ansiConsole;

        public async ValueTask ExecuteAsync(IConsole console)
        {
            _ansiConsole = console.CreateAnsiConsole();

            ValidateUserHasSelectedApps();

            var downloadArgs = new DownloadArguments
            {
                NoCache = NoLocalCache ?? default(bool)
            };
            using var steamManager = new SteamManager(_ansiConsole, downloadArgs);

            try
            {
                await steamManager.InitializeAsync();
                await steamManager.SetupBenchmarkAsync(AppIds.ToList(), BenchmarkAllOwnedGames ?? default(bool), UseSelectedApps ?? default(bool));
            }
            finally
            {
                steamManager.Shutdown();
            }
        }

        // Validates that the user has selected at least 1 app
        private void ValidateUserHasSelectedApps()
        {
            if (AppIds != null && AppIds.Any())
            {
                return;
            }
            if (BenchmarkAllOwnedGames ?? default(bool))
            {
                return;
            }
            if (UseSelectedApps ?? default(bool))
            {
                return;
            }

            _ansiConsole.MarkupLine(Red("No apps have been selected for prefill! At least 1 app is required!"));
            _ansiConsole.Markup(Red($"See flags {LightYellow("--appid")}, {LightYellow("--all")} and {LightYellow("--use-selected")} to interactively choose which apps to prefill"));

            throw new CommandException(".", 1, true);
        }
    }
}
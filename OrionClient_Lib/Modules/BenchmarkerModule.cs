﻿using Blake2Sharp;
using NLog;
using OrionClientLib.Hashers;
using OrionClientLib.Hashers.Models;
using OrionClientLib.Modules.Models;
using Spectre.Console;
using Spectre.Console.Rendering;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Modules
{
    public class BenchmarkerModule : IModule
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        private enum Step { Initial, Benchmarking };

        public string Name { get; } = "Benchmarker";

        private const int _totalSeconds = 30;
        private bool _stop = false;
        private CancellationTokenSource _currentTokenSource;
        private Step _currentStep = Step.Initial;
        private int _hasherIndex = 0;
        private List<HasherInfo> _chosenHashers;
        private Table _render = null;
        private bool _finished = false;

        public async Task<(bool, string)> InitializeAsync(Data data)
        {
            _stop = false;
            _currentStep = Step.Initial;
            _chosenHashers = null;
            _hasherIndex = 0;
            _render = null;
            _finished = false;

            return await HandleHashSelection(data);
        }

        public async Task<ExecuteResult> ExecuteAsync(Data data)
        {
            IRenderable renderer = await HandleBenchmarking(data);

            return new ExecuteResult
            {
                Exited = _stop,
                Renderer = renderer
            };
        }

        private async Task<(bool, string)> HandleHashSelection(Data data)
        {
            MultiSelectionPrompt<IHasher> hasherPrompt = new MultiSelectionPrompt<IHasher>();
            hasherPrompt.Title = "Choose hashers to benchmark";

            hasherPrompt.UseConverter((hasher) => $"{hasher.Name} - {hasher.Description}");
            hasherPrompt.AddChoices(data.Hashers.Where(x => x is not DisabledHasher));

            try
            {
                _chosenHashers = (await hasherPrompt.ShowAsync(AnsiConsole.Console, GetNewToken())).Select(x => new HasherInfo { Hasher = x }).ToList();

                if (_chosenHashers.Count == 0)
                {
                    return (false, String.Empty);
                }
            }
            catch (TaskCanceledException)
            {
                return (false, String.Empty);
            }

            return (true, String.Empty);
        }

        private async Task<IRenderable> HandleBenchmarking(Data data)
        {
            if (_finished)
            {
                return _render;
            }

            HasherInfo currentHasherInfo = _chosenHashers[_hasherIndex];
            IHasher currentHasher = currentHasherInfo.Hasher;

            if (_stop)
            {
                await currentHasher.StopAsync();
                currentHasher.OnHashrateUpdate -= CurrentHasher_OnHashrateUpdate;

                return null;
            }

            if (_render == null)
            {
                var deviceTable = new Table();
                deviceTable.AddColumn(new TableColumn("Hasher").Centered());
                deviceTable.AddColumn(new TableColumn("Average Hashrate").Centered());
                deviceTable.AddColumn(new TableColumn("Min Hashrate").Centered());
                deviceTable.AddColumn(new TableColumn("Max Hashrate").Centered());
                deviceTable.AddColumn(new TableColumn("Remaining Time").Centered());
                deviceTable.Expand();
                deviceTable.ShowRowSeparators = true;

                for (int i = 0; i < _chosenHashers.Count; i++)
                {
                    HasherInfo hasher = _chosenHashers[i];

                    deviceTable.AddRow($"{hasher.Hasher.Name} ({data.Settings.CPUThreads} threads)",
                        $"-",
                        $"-",
                        $"-",
                        $"-");
                }

                _render = deviceTable;
            }

            if (!currentHasher.Initialized)
            {
                _logger.Log(LogLevel.Debug, $"Running hasher: {currentHasher.Name} for {_totalSeconds}s");

                currentHasher.OnHashrateUpdate += CurrentHasher_OnHashrateUpdate;
                currentHasher.Initialize(null, data.Settings.CPUThreads);
                byte[] challenge = new byte[32];
                RandomNumberGenerator.Fill(challenge);

                currentHasher.NewChallenge(0, challenge, 0, ulong.MaxValue);
            }

            //Allow each hasher to run for 30 seconds in total
            if (currentHasher.CurrentChallengeTime.TotalSeconds > _totalSeconds && currentHasher.Initialized)
            {
                await currentHasher.StopAsync();
                currentHasher.OnHashrateUpdate -= CurrentHasher_OnHashrateUpdate;
                ++_hasherIndex;

                if (currentHasherInfo.CurrentRate != null)
                {
                    _logger.Log(LogLevel.Info, $"Hasher: {currentHasherInfo.Hasher.Name}. " +
                        $"Average: {currentHasherInfo.CurrentRate.ChallengeSolutionsPerSecond}. " +
                        $"Min: {currentHasherInfo.CurrentRate.SolutionsPerSecond}. " +
                        $"Max: {currentHasherInfo.CurrentRate.SolutionsPerSecond}");
                }

                if (_hasherIndex >= _chosenHashers.Count)
                {
                    _finished = true;
                    _logger.Log(LogLevel.Info, $"Benchmark complete. Ctrl + C to return to menu");
                }
            }

            return _render;
        }

        void CurrentHasher_OnHashrateUpdate(object? sender, HashrateInfo e)
        {
            //Ignore first iteration due to slow jit compilation
            if (e.ChallengeSolutions == e.NumSolutions)
            {
                return;
            }

            HasherInfo currentInfo = _chosenHashers[_hasherIndex];
            currentInfo.MinRate ??= e;
            currentInfo.MaxRate ??= e;

            if (currentInfo.MinRate == null || currentInfo.MinRate.SolutionsPerSecond > e.SolutionsPerSecond)
            {
                currentInfo.MinRate = e;
            }

            if (currentInfo.MaxRate == null || currentInfo.MaxRate.SolutionsPerSecond < e.SolutionsPerSecond)
            {
                currentInfo.MaxRate = e;
            }

            currentInfo.CurrentRate = e;

            //Update data
            _render.UpdateCell(_hasherIndex, 1, currentInfo.CurrentRate.ChallengeSolutionsPerSecond.ToString());
            _render.UpdateCell(_hasherIndex, 2, currentInfo.MinRate.SolutionsPerSecond.ToString());
            _render.UpdateCell(_hasherIndex, 3, currentInfo.MaxRate.SolutionsPerSecond.ToString());
            _render.UpdateCell(_hasherIndex, 4, $"{Math.Max(0, _totalSeconds - currentInfo.CurrentRate.TotalTime.TotalSeconds):0.00}s");

            //Console.WriteLine($"{currentInfo.Hasher.Name} -- Average: {currentInfo.CurrentRate.SolutionsPerSecond}, Min: {currentInfo.MinRate.SolutionsPerSecond}, Max: {currentInfo.MaxRate.SolutionsPerSecond}. Runtime: {currentInfo.CurrentRate.TotalTime}");
        }

        public async Task ExitAsync()
        {
            _stop = true;
            _currentTokenSource?.Cancel();
        }

        private CancellationToken GetNewToken()
        {
            _currentTokenSource = new CancellationTokenSource();
            return _currentTokenSource.Token;
        }

        private class HasherInfo
        {
            public IHasher Hasher { get; set; }
            public HashrateInfo MinRate { get; set; }
            public HashrateInfo MaxRate { get; set; }
            public HashrateInfo CurrentRate { get; set; }
        }
    }
}

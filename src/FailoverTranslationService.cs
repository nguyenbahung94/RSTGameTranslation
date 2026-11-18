using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RSTGameTranslation
{
    public sealed class FailoverTranslationService : ITranslationService
    {
        private static readonly string[] Backups = { "Microsoft", "Google Translate" };
        private static readonly TimeSpan StuckAfter = TimeSpan.FromSeconds(5);

        private readonly string _primaryName;
        private readonly ITranslationService _primary;

        public bool AnsweredByBackup { get; private set; }

        public FailoverTranslationService(string primaryName, ITranslationService primary)
        {
            _primaryName = primaryName;
            _primary = primary;
        }

        public async Task<string?> TranslateAsync(string jsonData, string prompt)
        {
            Task<string?> primary = RunAsync(_primaryName, _primary, jsonData, prompt);
            var running = new List<Task<string?>> { primary };
            string current = _primaryName;
            foreach (string backup in Backups.Where(s => s != _primaryName))
            {
                var (answer, from) = await FirstAnswerAsync(running, StuckAfter);
                if (answer != null)
                    return Accept(answer, from, primary);

                Console.WriteLine(running.Count == 0
                    ? $"[Failover] {current} failed: translating with {backup}"
                    : $"[Failover] No answer after {StuckAfter.TotalSeconds:0} s: also asking {backup}");
                running.Add(RunAsync(backup, TranslationServiceFactory.CreateService(backup), jsonData, prompt));
                current = backup;
            }
            var (last, lastFrom) = await FirstAnswerAsync(running, Timeout.InfiniteTimeSpan);
            return last == null ? null : Accept(last, lastFrom, primary);
        }

        private string Accept(string answer, Task<string?>? from, Task<string?> primary)
        {
            if (from != primary)
                AnsweredByBackup = true;
            return answer;
        }

        private static async Task<string?> RunAsync(string name, ITranslationService service, string jsonData, string prompt)
        {
            try
            {
                string? result = await service.TranslateAsync(jsonData, prompt);
                return string.IsNullOrEmpty(result) ? null : result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Failover] {name} threw: {ex.Message}");
                return null;
            }
        }

        private static async Task<(string? Answer, Task<string?>? From)> FirstAnswerAsync(List<Task<string?>> running, TimeSpan wait)
        {
            Task deadline = Task.Delay(wait);
            while (running.Count > 0)
            {
                Task finished = await Task.WhenAny(running.Cast<Task>().Append(deadline));
                if (finished == deadline)
                    return (null, null);

                var request = (Task<string?>)finished;
                running.Remove(request);
                string? answer = await request;
                if (answer != null)
                    return (answer, request);
            }
            return (null, null);
        }
    }
}

    public static class ConsoleInteractor
    {
        private static TaskCompletionSource<string?>? _pendingTcs;
        private static readonly object _lock = new();

        public static bool HasActiveQuestion { get { lock (_lock) return _pendingTcs != null; } }

        public static Task<string?> AskAsync(string prompt, TimeSpan timeout, string? defaultAnswer = null)
        {
            TaskCompletionSource<string?> tcs;
            lock (_lock)
            {
                if (_pendingTcs != null)
                    throw new InvalidOperationException("Another question is already active.");
                _pendingTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                tcs = _pendingTcs;
            }

            Console.Write($"{prompt} ");

            var cts = new CancellationTokenSource();
            cts.CancelAfter(timeout);

            // IMPORTANT: dispose this registration once the task completes
            var reg = cts.Token.Register(() =>
            {
                lock (_lock)
                {
                    if (_pendingTcs == tcs && !tcs.Task.IsCompleted)
                    {
                        _pendingTcs = null;
                        tcs.TrySetResult(defaultAnswer);
                        Console.WriteLine();
                        Console.WriteLine("(no response, continuing…)");
                    }
                }
            });

            _ = tcs.Task.ContinueWith(_ => { reg.Dispose(); cts.Dispose(); }, TaskScheduler.Default);
            return tcs.Task;
        }

        public static bool TryAnswer(string line)
        {
            TaskCompletionSource<string?>? tcs;
            lock (_lock)
            {
                tcs = _pendingTcs;
                _pendingTcs = null;
            }
            if (tcs == null) return false;
            tcs.TrySetResult(line);
            return true;
        }

        // handy sugar
        public static async Task<bool> AskYesNoAsync(string prompt, TimeSpan timeout, bool @default = false)
        {
            var defText = @default ? "Y/n" : "y/N";
            var ans = (await AskAsync($"{prompt} ({defText})", timeout, @default ? "y" : "n"))?
                      .Trim().ToLowerInvariant();
            return ans is "y" or "yes";
        }
    }

using Dle.SecurityTests.Fuzzing;
using SharpFuzz;

namespace Dle.FuzzDriver;

public static class Program
{
    public static void Main(string[] args)
    {
        string target = args.Length > 0 ? args[0].TrimStart('-').ToLowerInvariant() : "routing-rules";

        if (target is "help" or "h" or "?")
        {
            Console.WriteLine("Deep Link Engine — SharpFuzz coverage-guided fuzz driver");
            Console.WriteLine("Usage: dotnet run --project tests/Dle.FuzzDriver -- [target]");
            Console.WriteLine("Available targets:");
            Console.WriteLine("  url               Fuzzes target URL and host validation syntax");
            Console.WriteLine("  user-agent        Fuzzes client user-agent classifier regexes");
            Console.WriteLine("  routing-rules     Fuzzes routing_rules JSON parser and validator");
            Console.WriteLine("  install-referrer  Fuzzes Play install referrer parser");
            return;
        }

        Console.WriteLine($"Starting SharpFuzz LibFuzzer loop for target: '{target}'...");

        switch (target)
        {
            case "url":
                Fuzzer.LibFuzzer.Run(FuzzTargets.Url);
                break;

            case "user-agent" or "useragent":
                Fuzzer.LibFuzzer.Run(FuzzTargets.UserAgent);
                break;

            case "routing-rules" or "routingrules" or "rules":
                Fuzzer.LibFuzzer.Run(FuzzTargets.RoutingRules);
                break;

            case "install-referrer" or "installreferrer" or "referrer":
                Fuzzer.LibFuzzer.Run(FuzzTargets.InstallReferrer);
                break;

            default:
                Console.Error.WriteLine($"Unknown fuzz target '{target}'. Available targets: url, user-agent, routing-rules, install-referrer");
                Environment.Exit(1);
                break;
        }
    }
}

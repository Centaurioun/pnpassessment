﻿using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Process.Commands;
using PnP.Scanning.Process.Services;
using Serilog;
using Serilog.Events;
using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;

namespace PnP.Scanning.Process
{
    internal class Program
    {
        internal static async Task Main(string[] args)
        {
            bool isCliProcess = true;

            if (args.Length > 0 && args[0].Equals("scanner", StringComparison.OrdinalIgnoreCase))
            {
                isCliProcess = false;
            }

            // Launching PnP.Scanning.Process.exe as CLI
            if (isCliProcess)
            {
                // Configure needed services
                var host = ConfigureCliHost(args);
                
                // Get ProcessManager instance from the cli executable
                var processManager = host.Services.GetRequiredService<ScannerManager>();

                var root = new RootCommandHandler(processManager).Create();
                var builder = new CommandLineBuilder(root);
                var parser = builder.UseDefaults().Build();

                if (args.Length == 0)
                {
                    AnsiConsole.Write(new FigletText("Microsoft 365 Scanner").Centered().Color(Color.Green));
                    AnsiConsole.WriteLine("");
                    AnsiConsole.Markup("Execute a command [gray](<enter> to quit)[/]: ");
                    var consoleInput = Console.ReadLine();

                    // Possible enhancement: build custom tab completion for the "console mode"
                    // To get suggestions
                    // var result = parser.Parse(consoleInput).GetCompletions();
                    // Sample to start from: https://www.codeproject.com/Articles/1182358/Using-Autocomplete-in-Windows-Console-Applications

                    while (!string.IsNullOrEmpty(consoleInput))
                    {
                        await parser.InvokeAsync(consoleInput);

                        AnsiConsole.WriteLine("");
                        AnsiConsole.Markup("Execute a command [gray](<enter> to quit)[/]: ");
                        consoleInput = Console.ReadLine();
                    }
                }
                else
                {
                    await parser.InvokeAsync(args);
                }
            }
            else
            {
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
                Log.Logger = new LoggerConfiguration()
                           .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
                           .Enrich.FromLogContext()
                           .WriteTo.Console()
                           .WriteTo.File($"log_{timestamp}.txt")
                           // Duplicate all log entries generated for an actual scan component
                           // to a separate log file in the folder per scan
                           .WriteTo.Map("ScanId", (scanId, wt) => wt.File($"./{scanId}/log_{scanId}.txt"))
                           .CreateLogger();

                try
                {
                    // Launching PnP.Scanning.Process.exe as Kestrel web server to which we'll communicate via gRPC
                    // Get port on which the orchestrator has to listen
                    int orchestratorPort = ScannerManager.DefaultScannerPort;
                    if (args.Length >= 2)
                    {
                        if (int.TryParse(args[1], out int providedPort))
                        {
                            orchestratorPort = providedPort;
                        }
                    }

                    Log.Information($"Starting scanner on port {orchestratorPort}");

                    // Add and configure needed services
                    var host = ConfigureScannerHost(args, orchestratorPort);

                    Log.Information($"Started scanner on port {orchestratorPort}");

                    await host.RunAsync();
                }
                catch (Exception ex)
                {
                    Log.Fatal(ex, "Scanner terminated unexpectedly");
                }
                finally
                {
                    Log.CloseAndFlush();
                }
            }
        }


        private static IHost ConfigureCliHost(string[] args)
        {
            return Host.CreateDefaultBuilder(args)
                       .ConfigureServices(services =>
                       {
                           services.AddSingleton<ScannerManager>();
                       })
                       .UseConsoleLifetime()
                       .Build();
        }

        private static IHost ConfigureScannerHost(string[] args, int orchestratorPort)
        {
            return Host.CreateDefaultBuilder(args)
                  .UseSerilog() 
                  .ConfigureWebHostDefaults(webBuilder =>
                  {
                      webBuilder.UseStartup<Startup<Scanner>>();

                      webBuilder.ConfigureKestrel(options =>
                      {
                          options.ListenLocalhost(orchestratorPort, listenOptions =>
                          {
                              listenOptions.Protocols = HttpProtocols.Http2;
                          });
                      });

                      webBuilder.ConfigureServices(services =>
                      {
                          services.AddSingleton<StorageManager>();
                          services.AddSingleton<ScanManager>();
                          services.AddTransient<SiteEnumerationManager>();
                          services.AddTransient<ReportManager>();
                      });

                  })

                  .UseConsoleLifetime()
                  .Build();
        }
    }
}

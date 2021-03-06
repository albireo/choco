﻿// Copyright © 2011 - Present RealDimensions Software, LLC
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// 
// You may obtain a copy of the License at
// 
// 	http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

namespace chocolatey.infrastructure.app.services
{
    using System.IO;
    using System.Linq;
    using adapters;
    using commandline;
    using configuration;
    using domain;
    using filesystem;
    using infrastructure.commands;
    using logging;
    using results;
    using Environment = System.Environment;

    public class PowershellService : IPowershellService
    {
        private readonly IFileSystem _fileSystem;
        private readonly string _customImports;

        public PowershellService(IFileSystem fileSystem)
            : this(fileSystem, new CustomString(string.Empty))
        {
        }

        /// <summary>
        ///   Initializes a new instance of the <see cref="PowershellService" /> class.
        /// </summary>
        /// <param name="fileSystem">The file system.</param>
        /// <param name="customImports">The custom imports. This should be everything you need minus the &amp; to start and the ending semi-colon.</param>
        public PowershellService(IFileSystem fileSystem, CustomString customImports)
        {
            _fileSystem = fileSystem;
            _customImports = customImports;
        }

        public void noop_action(PackageResult packageResult, CommandNameType command)
        {
            var file = "chocolateyInstall.ps1";
            switch (command)
            {
                case CommandNameType.uninstall:
                    file = "chocolateyUninstall.ps1";
                    break;
            }

            var packageDirectory = packageResult.InstallLocation;
            var installScript = _fileSystem.get_files(packageDirectory, file, SearchOption.AllDirectories);
            if (installScript.Count() != 0)
            {
                var chocoInstall = installScript.FirstOrDefault();

                this.Log().Info("Would have run '{0}':".format_with(chocoInstall));
                this.Log().Warn(_fileSystem.read_file(chocoInstall).escape_curly_braces());
            }
        }

        public void install_noop(PackageResult packageResult)
        {
            noop_action(packageResult, CommandNameType.install);
        }

        public bool install(ChocolateyConfiguration configuration, PackageResult packageResult)
        {
            return run_action(configuration, packageResult, CommandNameType.install);
        }

        public void uninstall_noop(PackageResult packageResult)
        {
            noop_action(packageResult, CommandNameType.uninstall);
        }

        public bool uninstall(ChocolateyConfiguration configuration, PackageResult packageResult)
        {
            return run_action(configuration, packageResult, CommandNameType.uninstall);
        }

        public string wrap_script_with_module(string script, ChocolateyConfiguration config)
        {
            var installerModules = _fileSystem.get_files(ApplicationParameters.InstallLocation, "chocolateyInstaller.psm1", SearchOption.AllDirectories);
            var installerModule = installerModules.FirstOrDefault();
            var scriptRunners = _fileSystem.get_files(ApplicationParameters.InstallLocation, "chocolateyScriptRunner.ps1", SearchOption.AllDirectories);
            var scriptRunner = scriptRunners.FirstOrDefault();
            // removed setting all errors to terminating. Will cause too
            // many issues in existing packages, including upgrading
            // Chocolatey from older POSH client due to log errors
            //$ErrorActionPreference = 'Stop';
            return "[System.Threading.Thread]::CurrentThread.CurrentCulture = '';[System.Threading.Thread]::CurrentThread.CurrentUICulture = ''; & import-module -name '{0}';{2} & '{1}' {3}"
                .format_with(
                    installerModule,
                    scriptRunner,
                    string.IsNullOrWhiteSpace(_customImports) ? string.Empty : "& {0}".format_with(_customImports.EndsWith(";") ? _customImports : _customImports + ";"),
                    get_script_arguments(script,config)
                );
        }

        private string get_script_arguments(string script, ChocolateyConfiguration config)
        {
            return "-packageScript '{0}' -installArguments '{1}' -packageParameters '{2}'{3}{4}".format_with(
                script,
                config.InstallArguments,
                config.PackageParameters,
                config.ForceX86 ? " -forceX86" : string.Empty,
                config.OverrideArguments ? " -overrideArgs" : string.Empty
             );
        }

        public bool run_action(ChocolateyConfiguration configuration, PackageResult packageResult, CommandNameType command)
        {
            var installerRun = false;

            var file = "chocolateyInstall.ps1";
            switch (command)
            {
                case CommandNameType.uninstall:
                    file = "chocolateyUninstall.ps1";
                    break;
            }

            var packageDirectory = packageResult.InstallLocation;
            if (packageDirectory.is_equal_to(ApplicationParameters.InstallLocation) || packageDirectory.is_equal_to(ApplicationParameters.PackagesLocation))
            {
                packageResult.Messages.Add(
                    new ResultMessage(
                        ResultType.Error,
                        "Install location is not specific enough, cannot run PowerShell script:{0} Erroneous install location captured as '{1}'".format_with(Environment.NewLine, packageResult.InstallLocation)
                        )
                    );

                return false;
            }

            if (!_fileSystem.directory_exists(packageDirectory))
            {
                packageResult.Messages.Add(new ResultMessage(ResultType.Error, "Package install not found:'{0}'".format_with(packageDirectory)));
                return installerRun;
            }

            var powershellScript = _fileSystem.get_files(packageDirectory, file, SearchOption.AllDirectories);
            if (powershellScript.Count() != 0)
            {
                var chocoPowerShellScript = powershellScript.FirstOrDefault();

                var failure = false;

                var package = packageResult.Package;
                Environment.SetEnvironmentVariable(ApplicationParameters.ChocolateyInstallEnvironmentVariableName, ApplicationParameters.InstallLocation);
                Environment.SetEnvironmentVariable("CHOCOLATEY_VERSION", configuration.Information.ChocolateyVersion);
                Environment.SetEnvironmentVariable("CHOCOLATEY_VERSION_PRODUCT", configuration.Information.ChocolateyProductVersion);
                Environment.SetEnvironmentVariable("OS_PLATFORM", configuration.Information.PlatformType.get_description_or_value());
                Environment.SetEnvironmentVariable("OS_VERSION", configuration.Information.PlatformVersion.to_string());
                Environment.SetEnvironmentVariable("OS_NAME", configuration.Information.PlatformName.to_string());
                // experimental until we know if this value returns correctly based on the OS and not the current process.
                Environment.SetEnvironmentVariable("OS_IS64BIT", configuration.Information.Is64Bit ? "true" : "false");
                Environment.SetEnvironmentVariable("IS_ADMIN", configuration.Information.IsUserAdministrator ? "true" : "false");
                Environment.SetEnvironmentVariable("IS_PROCESSELEVATED", configuration.Information.IsProcessElevated ? "true" : "false");
                Environment.SetEnvironmentVariable("chocolateyPackageName", package.Id);
                Environment.SetEnvironmentVariable("packageName", package.Id);
                Environment.SetEnvironmentVariable("chocolateyPackageVersion", package.Version.to_string());
                Environment.SetEnvironmentVariable("packageVersion", package.Version.to_string());
                Environment.SetEnvironmentVariable("chocolateyPackageFolder", packageDirectory);
                Environment.SetEnvironmentVariable("packageFolder", packageDirectory);
                Environment.SetEnvironmentVariable("installArguments", configuration.InstallArguments);
                Environment.SetEnvironmentVariable("installerArguments", configuration.InstallArguments);
                Environment.SetEnvironmentVariable("chocolateyInstallArguments", configuration.InstallArguments);
                Environment.SetEnvironmentVariable("packageParameters", configuration.PackageParameters);
                Environment.SetEnvironmentVariable("chocolateyPackageParameters", configuration.PackageParameters);
                if (configuration.ForceX86)
                {
                    Environment.SetEnvironmentVariable("chocolateyForceX86", "true");
                }
                if (configuration.OverrideArguments)
                {
                    Environment.SetEnvironmentVariable("chocolateyInstallOverride", "true");
                }

                Environment.SetEnvironmentVariable("TEMP", configuration.CacheLocation);

                //verify how not silent is passed
                //if (configuration.NotSilent)
                //{
                //    Environment.SetEnvironmentVariable("installerArguments", "  ");
                //    Environment.SetEnvironmentVariable("chocolateyInstallOverride", "true");
                //}
                if (configuration.Debug)
                {
                    Environment.SetEnvironmentVariable("ChocolateyEnvironmentDebug", "true");
                }
                if (configuration.Verbose)
                {
                    Environment.SetEnvironmentVariable("ChocolateyEnvironmentVerbose", "true");
                }
                //todo:if (configuration.NoOutput)
                //{
                //    Environment.SetEnvironmentVariable("ChocolateyEnvironmentQuiet","true");
                //}

                this.Log().Debug(ChocolateyLoggers.Important, "Contents of '{0}':".format_with(chocoPowerShellScript));
                string chocoPowerShellScriptContents = _fileSystem.read_file(chocoPowerShellScript);
                this.Log().Debug(chocoPowerShellScriptContents.escape_curly_braces());

                bool shouldRun = !configuration.PromptForConfirmation;

                if (!shouldRun)
                {
                    this.Log().Info(ChocolateyLoggers.Important, () => "The package {0} wants to run '{1}'.".format_with(package.Id, _fileSystem.get_file_name(chocoPowerShellScript)));
                    this.Log().Info(ChocolateyLoggers.Important, () => "Note: If you don't run this script, the installation will fail.");
                    
                    var selection = InteractivePrompt.prompt_for_confirmation(@"Do you want to run the script?", new[] {"yes", "no", "print"}, defaultChoice: null, requireAnswer: true);

                    if (selection.is_equal_to("print"))
                    {
                        this.Log().Info(ChocolateyLoggers.Important, "------ BEGIN SCRIPT ------");
                        this.Log().Info(() => "{0}{1}{0}".format_with(Environment.NewLine, chocoPowerShellScriptContents.escape_curly_braces()));
                        this.Log().Info(ChocolateyLoggers.Important, "------- END SCRIPT -------");
                        selection = InteractivePrompt.prompt_for_confirmation(@"Do you want to run this script?", new[] { "yes", "no" }, defaultChoice: null, requireAnswer: true);
                    }

                    if (selection.is_equal_to("yes")) shouldRun = true;
                    if (selection.is_equal_to("no"))
                    {
                        Environment.ExitCode = 1;
                        packageResult.Messages.Add(new ResultMessage(ResultType.Error, "User cancelled powershell portion of installation for '{0}'.{1} Use skip to install without run.".format_with(powershellScript.FirstOrDefault(), Environment.NewLine)));
                    }
                }

                if (shouldRun)
                {
                    installerRun = true;
                    var exitCode = PowershellExecutor.execute(
                        wrap_script_with_module(chocoPowerShellScript, configuration),
                        _fileSystem,
                        configuration.CommandExecutionTimeoutSeconds,
                        (s, e) =>
                            {
                                if (string.IsNullOrWhiteSpace(e.Data)) return;
                                //inspect for different streams
                                if (e.Data.StartsWith("DEBUG:"))
                                {
                                    this.Log().Debug(() => " " + e.Data);
                                }
                                else if (e.Data.StartsWith("WARNING:"))
                                {
                                    this.Log().Warn(() => " " + e.Data);
                                }
                                else if (e.Data.StartsWith("VERBOSE:"))
                                {
                                    this.Log().Info(ChocolateyLoggers.Verbose, () => " " + e.Data);
                                }
                                else
                                {
                                    this.Log().Info(() => " " + e.Data);
                                }
                            },
                        (s, e) =>
                            {
                                if (string.IsNullOrWhiteSpace(e.Data)) return;
                                failure = true;
                                this.Log().Error(() => " " + e.Data);
                            });

                    if (failure)
                    {
                        Environment.ExitCode = exitCode;
                        packageResult.Messages.Add(new ResultMessage(ResultType.Error, "Error while running '{0}'.{1} See log for details.".format_with(powershellScript.FirstOrDefault(), Environment.NewLine)));
                    }
                    packageResult.Messages.Add(new ResultMessage(ResultType.Note, "Ran '{0}'".format_with(chocoPowerShellScript)));
                }
            }

            return installerRun;
        }
    }
}
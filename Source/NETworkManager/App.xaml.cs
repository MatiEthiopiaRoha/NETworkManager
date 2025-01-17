﻿using NETworkManager.Utilities;
using NETworkManager.Settings;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using NETworkManager.Profiles;
using NETworkManager.Localization;
using System.IO;
using log4net;

namespace NETworkManager;

/* 
 * Class: App
 * 1) Get command line args
 * 2) Detect current configuration
 * 3) Get assembly info
 * 4) Load settings
 * 5) Load localization / language
 * 
 * Class: MainWindow
 * 6) Load appearance
 * 7) Load profiles                 
 */

public partial class App
{
    private static readonly ILog _log = LogManager.GetLogger(typeof(App));

    // Single instance identifier
    private const string GUID = "6A3F34B2-161F-4F70-A8BC-A19C40F79CFB";
    private Mutex _mutex;
    private DispatcherTimer _dispatcherTimer;

    private bool _singleInstanceClose;

    public App()
    {
        ShutdownMode = ShutdownMode.OnMainWindowClose;
    }

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        var startLog = $@"
  _   _ _____ _____                    _    __  __                                   
 | \ | | ____|_   _|_      _____  _ __| | _|  \/  | __ _ _ __   __ _  __ _  ___ _ __ 
 |  \| |  _|   | | \ \ /\ / / _ \| '__| |/ / |\/| |/ _` | '_ \ / _` |/ _` |/ _ \ '__|
 | |\  | |___  | |  \ V  V / (_) | |  |   <| |  | | (_| | | | | (_| | (_| |  __/ |   
 |_| \_|_____| |_|   \_/\_/ \___/|_|  |_|\_\_|  |_|\__,_|_| |_|\__,_|\__, |\___|_|   
                                                                     |___/        
            
                                               by BornToBeRoot
                                               GitHub.com/BornToBeRoot/NETworkManager

                                               Version: {AssemblyManager.Current.Version}
";
        _log.Info(startLog);

        // Catch unhandled exception globally
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            _log.Fatal("Unhandled exception occured!");

            if (e.ExceptionObject != null)
                _log.Fatal($"Exception raised by: {e.ExceptionObject}");
        };

        // Wait until the previous instance has been closed (restart from ui)
        if (CommandLineManager.Current.RestartPid != -1)
        {
            _log.Info($"Waiting for another NETworkManager process with Pid {CommandLineManager.Current.RestartPid} to exit...");

            var processList = Process.GetProcesses();
            var process = processList.FirstOrDefault(x => x.Id == CommandLineManager.Current.RestartPid);
            process?.WaitForExit();

            _log.Info($"NETworkManager process with Pid {CommandLineManager.Current.RestartPid} has been exited.");
        }

        // Load (or initialize) settings
        try
        {
            _log.Info("Application settings are being loaded...");

            if (CommandLineManager.Current.ResetSettings)
                SettingsManager.Initialize();
            else
                SettingsManager.Load();
        }
        catch (InvalidOperationException ex)
        {
            _log.Error("Could not load application settings!");
            _log.Error(ex.Message + "-" + ex.StackTrace);

            // Create backup of corrupted file
            var destinationFile = $"{TimestampHelper.GetTimestamp()}_corrupted_" + SettingsManager.GetSettingsFileName();
            File.Copy(SettingsManager.GetSettingsFilePath(), Path.Combine(SettingsManager.GetSettingsFolderLocation(), destinationFile));
            _log.Info($"A backup of the corrupted settings file has been saved under {destinationFile}");

            // Initialize default application settings
            _log.Info("Initialize default application settings...");

            SettingsManager.Initialize();
            ConfigurationManager.Current.ShowSettingsResetNoteOnStartup = true;
        }

        // Upgrade settings if necessary
        Version settingsVersion = Version.Parse(SettingsManager.Current.Version);

        if (settingsVersion < AssemblyManager.Current.Version)
        {
            _log.Info($"Application settings are on version {settingsVersion} and will be upgraded to {AssemblyManager.Current.Version}");

            SettingsManager.Upgrade(settingsVersion, AssemblyManager.Current.Version);

            _log.Info($"Application settings upgraded to version {AssemblyManager.Current.Version}");
        }
        else
        {
            _log.Info($"Application settings are already on version {AssemblyManager.Current.Version}.");
        }

        // Initialize localization
        var localizationManager = LocalizationManager.GetInstance(SettingsManager.Current.Localization_CultureCode);
        Localization.Resources.Strings.Culture = localizationManager.Culture;

        _log.Info($"Application localization culture has been set to {localizationManager.Current.Code} (Settings value is \"{SettingsManager.Current.Localization_CultureCode}\").");

        // Show (localized) help window
        if (CommandLineManager.Current.Help)
        {
            _log.Info("Set StartupUri to CommandLineWindow.xaml...");
            StartupUri = new Uri("CommandLineWindow.xaml", UriKind.Relative);

            return;
        }

        // Create mutex (to detect single instance)
        _log.Info($"Try to acquire mutex with GUID {GUID} for single instance detection...");

        _mutex = new Mutex(true, "{" + GUID + "}");
        var mutexIsAcquired = _mutex.WaitOne(TimeSpan.Zero, true);

        _log.Info($"Mutex value for {GUID} is {mutexIsAcquired}");

        // Release mutex
        if (mutexIsAcquired)
            _mutex.ReleaseMutex();

        if (mutexIsAcquired || SettingsManager.Current.Window_MultipleInstances)
        {
            // Setup background job
            if (SettingsManager.Current.General_BackgroundJobInterval != 0)
            {
                _log.Info($"Setup background job with interval {SettingsManager.Current.General_BackgroundJobInterval} minute(s)...");

                _dispatcherTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMinutes(SettingsManager.Current.General_BackgroundJobInterval)
                };
                _dispatcherTimer.Tick += DispatcherTimer_Tick;
                _dispatcherTimer.Start();
            }
            else
            {
                _log.Info("Background job is disabled.");
            }

            // Setup ThreadPool for the application
            ThreadPool.GetMaxThreads(out var workerThreadsMax, out var completionPortThreadsMax);
            ThreadPool.GetMinThreads(out var workerThreadsMin, out var completionPortThreadsMin);

            var workerThreadsMinNew = workerThreadsMin + SettingsManager.Current.General_ThreadPoolAdditionalMinThreads;
            var completionPortThreadsMinNew = completionPortThreadsMin + SettingsManager.Current.General_ThreadPoolAdditionalMinThreads;

            if (workerThreadsMinNew > workerThreadsMax)
                workerThreadsMinNew = workerThreadsMax;

            if (completionPortThreadsMinNew > completionPortThreadsMax)
                completionPortThreadsMinNew = completionPortThreadsMax;

            if (ThreadPool.SetMinThreads(workerThreadsMinNew, completionPortThreadsMinNew))
                _log.Info($"ThreadPool min threads set to: workerThreads: {workerThreadsMinNew}, completionPortThreads: {completionPortThreadsMinNew}");
            else
                _log.Warn($"ThreadPool min threads could not be set to workerThreads: {workerThreadsMinNew}, completionPortThreads: {completionPortThreadsMinNew}");

            // Show splash screen
            if (SettingsManager.Current.SplashScreen_Enabled)
            {
                _log.Info("Show SplashScreen while application is loading...");
                new SplashScreen(@"SplashScreen.png").Show(true, true);
            }

            // Show main window
            _log.Info("Set StartupUri to MainWindow.xaml...");
            StartupUri = new Uri("MainWindow.xaml", UriKind.Relative);
        }
        else
        {
            // Bring the already running application into the foreground
            _log.Info("Another NETworkManager process is already running. Try to bring the window to the foreground...");
            SingleInstance.PostMessage((IntPtr)SingleInstance.HWND_BROADCAST, SingleInstance.WM_SHOWME, IntPtr.Zero, IntPtr.Zero);

            // Close the application                
            _singleInstanceClose = true;
            Shutdown();
        }
    }

    private void DispatcherTimer_Tick(object sender, EventArgs e)
    {
        _log.Info("Run background job...");

        Save();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);

        e.Cancel = true;

        Shutdown();
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        _log.Info("Exiting NETworkManager...");

        // Save settings, when the application is normally closed
        if (_singleInstanceClose || CommandLineManager.Current.Help)
            return;

        _log.Info("Stop background job (if it exists)...");
        _dispatcherTimer?.Stop();

        Save();

        _log.Info("Bye!");
    }

    private void Save()
    {
        if (SettingsManager.Current.SettingsChanged)
        {
            _log.Info("Save application settings...");
            SettingsManager.Save();
        }

        if (ProfileManager.ProfilesChanged)
        {
            _log.Info("Save current profiles...");
            ProfileManager.Save();
        }
    }
}

using System.CodeDom;
using System.Diagnostics;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Security.Principal;
using CurveMaster.include.HWiNFO;
using CurveMaster.include.RyzenUtil;
using Newtonsoft.Json;

namespace CurveMaster
{
    public class SerialHeartbeat(string COMPort)
    {
        private readonly CancellationTokenSource cts = new();
        private readonly SerialPort COM = new(COMPort, 115200)
        {
            Parity = Parity.None,
            StopBits = StopBits.One,
            DataBits = 8,
            Handshake = Handshake.None
        };

        private static Dictionary<string, object> WatchdogMonitorTrigger = new() 
        {
            { "status", true }
        };

        public void StartHeartbeat(int intervalMs = 10000)
        {
            COM.Open();
            Task.Run(() => HeartbeatLoop(intervalMs, COM, cts.Token));
        }

        private static async Task HeartbeatLoop(int intervalMs, SerialPort COM, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    COM.WriteLine($"{JsonConvert.SerializeObject(WatchdogMonitorTrigger)}");
                    await Task.Delay(intervalMs, token);
                }
            }
            catch (TaskCanceledException)
            {
                // Graceful exit
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in heartbeat: {ex.Message}");
            }
        }

        public void StopHeartbeat()
        {
            cts.Cancel();
        }

        public void Dispose()
        {
            StopHeartbeat();
            WatchdogMonitorTrigger["status"] = false;
            COM.WriteLine(JsonConvert.SerializeObject(WatchdogMonitorTrigger));
            COM.Close();
            COM.Dispose();
        }
    }

    class Program
    {
        private static readonly Dictionary<int, Dictionary<string, uint>> SensorIndexes = [];
        private static readonly uint PPTIndex;
        private static readonly uint AvgEffectiveClock;
        private static readonly uint L3Clocks;
        private static SerialHeartbeat? Watchdog;

        static Program()
        {
            if (!IsAdministrator())
            {
                Log("This application must be run as an administrator.");
                Log("Press any key to exit...");
                Console.ReadKey();
                Environment.Exit(1);
            }

            (SensorIndexes, PPTIndex, AvgEffectiveClock, L3Clocks) = HWiNFOEZ.GetSensorIndexes();
        }

        private static readonly ProcessStartInfo ycruncherInfo = new()
        {
            FileName = "y-cruncher\\y-cruncher.exe",
            Arguments = "config y-cruncher\\test.cfg",
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        
        private static readonly string ycruncherConfigTemplate = @"
{{
    Action : ""StressTest""
    StressTest : {{
        AllocateLocally : true
        LogicalCores : [""{0}""] 
        TotalMemory : 12318351360
        SecondsPerTest : 120
        SecondsTotal : 0
        StopOnError : true
        Tests : [""{1}""] 
    }}
}}";

        private static readonly ProcessStartInfo SCEWinReadInfo = new()
        {
            FileName = "SCEWIN\\SCEWIN_64.exe",
            Arguments = "/o /s SCEWIN\\nvram.txt",
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        private static readonly ProcessStartInfo SCEWinWriteInfo = new()
        {
            FileName = "SCEWIN\\SCEWIN_64.exe",
            Arguments = "/i /s SCEWIN\\nvram.txt",
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        private static readonly ProcessStartInfo RebootCMDInfo = new()
        {
            FileName = "shutdown",
            Arguments = "/r /t 0",
            CreateNoWindow = true,
            UseShellExecute = false
        };

        private static readonly ProcessStartInfo AddToTaskSchedulerInfo = new()
        {
            FileName = "schtasks",
            Arguments = $"/create /tn \"Ryzen Curve Master\" /tr \"\\\"{Environment.ProcessPath}\\\"\" /sc onlogon /rl highest /f",
            CreateNoWindow = true,
            UseShellExecute = false
        };

        private static readonly Process ycruncher = new() {StartInfo = ycruncherInfo};
        private static readonly Process SCEWinReadCMD = new() {StartInfo = SCEWinReadInfo};
        private static readonly Process SCEWinWriteCMD = new() {StartInfo = SCEWinWriteInfo};
        private static readonly Process RebootCMD = new() {StartInfo = RebootCMDInfo};
        private static readonly Process AddToTaskSchedulerCMD = new() {StartInfo = AddToTaskSchedulerInfo};

        private static readonly OCProcessState State = OCProcessState.LoadState();

        private static bool FinallyDone = false;

        static void Main()
        {
            try
            {
                Directory.SetCurrentDirectory(Path.GetDirectoryName(Environment.ProcessPath)!);
                Console.CancelKeyPress += new ConsoleCancelEventHandler(CleanExitEvent);

                if (State.WatchdogCOMPort != "")
                {
                    Log($"Sending heartbeat on {State.WatchdogCOMPort}...");
                    Watchdog = new SerialHeartbeat(State.WatchdogCOMPort);
                    Watchdog.StartHeartbeat();
                }

                while (!FinallyDone)
                {
                    switch (State.CurrentStep)
                    {
                        case "init":
                            Log("Step 0: Collect system information, warn user, and verify capabilities...");
                            Init();
                            State.CurrentStep = "vid_sync";
                            State.SaveState();
                            break;

                        case "vid_sync":
                            AllowEarlyQuit();
                            CheckPBOEFIVars();
                            Log("Step 1: Synchronizing core VIDs under y-cruncher BKT...");
                            SynchronizeVIDs();
                            State.CurrentStep = "single_core_test_campaign";
                            break;

                        case "single_core_test_campaign":
                            AllowEarlyQuit();
                            CheckPBOEFIVars();
                            Log("Step 2: Lowering Curve Optimizer core-by-core and testing 1T stability...");
                            SingleCoreTesting();
                            break;
                    }
                }

                PrintClockImprovement();
                CleanExit();
            }

            catch (Exception ex)
            {
                Console.WriteLine("Unhandled Exception:");
                Console.WriteLine(ex);
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey();
                CleanExit();
            }
        }

        static void Log(string message)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        }


        private static void CheckPBOEFIVars()
        {
            // For some ungodly reason, it seems that changing curve shaper values changes the PBO settings... So...
            // Might have to double reboot to do things right...

            bool RebootFlag = false;
            SCEWinRead();
            List<string> Lines = File.ReadAllLines("SCEWIN\\nvram.txt").ToList();
            int StartIndex = Lines.FindIndex(x => x.Equals("Setup Question	= Precision Boost Overdrive"));

            if (Lines[StartIndex + 5] == "Options	=*[FF]Auto	// Move \"*\" to the desired Option")
            {
                Lines[StartIndex + 5] = "Options	=[FF]Auto	// Move \"*\" to the desired Option";
                Lines[StartIndex + 8] = "         *[02]Advanced";
                Log("PBO EFI var not set to Advanced");
                RebootFlag = true;
            }

            StartIndex = Lines.FindIndex(x => x.Equals("Setup Question	= PBO Limits"));

            if (Lines[StartIndex + 5] == "Options	=*[FF]Auto	// Move \"*\" to the desired Option")
            {
                Lines[StartIndex + 5] = "Options	=[FF]Auto	// Move \"*\" to the desired Option";
                Lines[StartIndex + 7] = "         *[01]Motherboard";
                Log("PBO limits not set to Motherboard");
                RebootFlag = true;
            }

            StartIndex = Lines.FindIndex(x => x.Equals("Setup Question	= Precision Boost Overdrive Scalar Ctrl"));

            if (Lines[StartIndex + 5] == "Options	=*[FF]Auto	// Move \"*\" to the desired Option")
            {
                Lines[StartIndex + 5] = "Options	=[FF]Auto	// Move \"*\" to the desired Option";
                Lines[StartIndex + 6] = "         *[01]Manual";
                Log("PBO Scalar not set to manual");
                RebootFlag = true;
            }

            StartIndex = Lines.FindIndex(x => x.Equals("Setup Question	= Precision Boost Overdrive Scalar"));

            if (Lines[StartIndex + 5] == "Options	=[64]1X	// Move \"*\" to the desired Option")
            {
                Lines[StartIndex + 5] = "Options	=*[64]1X	// Move \"*\" to the desired Option";
                Lines[StartIndex + 6] = "         [C8]2X";
                Lines[StartIndex + 7] = "         [12C]3X";
                Lines[StartIndex + 8] = "         [190]4X";
                Lines[StartIndex + 9] = "         [1F4]5X";
                Lines[StartIndex + 10] = "         [258]6X";
                Lines[StartIndex + 11] = "         [2BC]7X";
                Lines[StartIndex + 12] = "         [320]8X";
                Lines[StartIndex + 13] = "         [384]9X";
                Lines[StartIndex + 14] = "         [3E8]10X";
                Log("PBO Scalar value not set to 1x");
                RebootFlag = true;
            }

            StartIndex = Lines.FindIndex(x => x.Equals("Setup Question	= CPU Boost Clock Override"));

            if (Lines[StartIndex + 5] == "Options	=*[00]Disabled	// Move \"*\" to the desired Option")
            {
                Lines[StartIndex + 5] = "Options	=[00]Disabled	// Move \"*\" to the desired Option";
                Lines[StartIndex + 6] = "         *[01]Enabled (Positive)";
                Log("Boost clock override not set to positive");
                RebootFlag = true;
            }

            StartIndex = Lines.FindIndex(x => x.Equals("Setup Question	= Max CPU Boost Clock Override(+)"));

            if (Lines[StartIndex + 5] != "Value	=<200>")
            {
                Lines[StartIndex + 5] = "Value	=<200>";
                Log("Boost clock override not maximized");
                RebootFlag = true;
            }

            StartIndex = Lines.FindIndex(x => x.Equals("Setup Question	= Curve Optimizer"));

            if (Lines[StartIndex + 5] == "Options	=*[00]Disable	// Move \"*\" to the desired Option")
            {
                Lines[StartIndex + 5] = "Options	=[00]Disable	// Move \"*\" to the desired Option";
                Lines[StartIndex + 7] = "         *[02]Per Core";
                Log("Curve Optimizer not set to per-core");
                RebootFlag = true;
            }

            if (RebootFlag) 
            {
                Log("Incorrect PBO EFI vars found, correcting and rebooting...");
                File.WriteAllLines("SCEWIN\\nvram.txt", Lines);
                SCEWinWrite();
                CleanExit(true);
            }
        }

        private static void CleanExitEvent(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            CleanExit();
        }

        private static void CleanExit(bool Reboot = false)
        {
            State.SaveState();
            Watchdog?.Dispose();
            try
            {
                ycruncher.Kill(true);
            }
            // Just the easiest way to deal with a situation where it was never started...
            catch {}
            ycruncher.Dispose();
            RyzenEZ.Dispose();
            HWiNFOEZ.Dispose();
            State.LastShutdownWasClean = true;
            State.SaveState();
            if (Reboot)
            {
                AllowEarlyQuit();
                RebootCMD.Start();
            }
            else Environment.Exit(0);
        }

        private static void AllowEarlyQuit()
        {
            //Log("Waiting one minute... Exit the window now if you don't want automation to continue.");
            //Thread.Sleep(MinutesToMilliseconds(1));
        }

        private static void StartYcruncher(string testType, string ycruncherCoreAffinity)
        {
            File.WriteAllText("y-cruncher\\test.cfg", string.Format(ycruncherConfigTemplate, Convert.ToString(ycruncherCoreAffinity), testType));
            ycruncher.Start();
        }

        private static int GetHighestVIDIndex(List<double> VIDs)
        {
            int HighestIndex = 0;
            for (int i = 1; i < VIDs.Count; i++)
            {
                if (VIDs[i] > VIDs[HighestIndex]) HighestIndex = i;
            }

            return HighestIndex;
        }

        private static void Init()
        {
            Log("Welcome to the Ryzen Curve Master!");
            Log("Your computer is about to be sacrificed to the throes of automation; Bluescreens, freezes, and other crashes may occur. Corrupted EFI vars may occur. Continue at your own peril.");
            Log("Please make sure that your current BIOS settings are accurate and correct, and that EFI vars are not password protected.");
            Log("If using an external watchdog, please enter the appropriate COM port below and hit enter. Otherwise, just hit enter to begin automation.");
            string[] ports = SerialPort.GetPortNames();
            Log("Available ports: " + string.Join(", ", ports));
            Console.Write("> ");
            string COMPort = Console.ReadLine()!;
            if (COMPort != "")
            {
                Log($"Sending heartbeat on {COMPort}...");
                State.WatchdogCOMPort = COMPort;
                Watchdog = new SerialHeartbeat(COMPort);
                Watchdog.StartHeartbeat();
            }
            Log("Testing that SCEWIN can read/write EFI vars...");
            SCEWinRead();
            ZeroCurveShaperValues();
            Log("SCEWIN successfully read EFI vars, attempting write...");
            SCEWinWrite();
            Log("SCEWIN successfully wrote EFI vars.");
            Log("Adding this executable to task scheduler... It will run automatically at every boot. You will be allowed one minute to interrupt it whenever it starts after a reboot.");
            AddToTaskSchedulerCMD.Start();
        }

        private static void SCEWinRead()
        {
            SCEWinReadCMD.Start();
            SCEWinReadCMD.WaitForExit();
            if (SCEWinReadCMD.ExitCode != 0)
            {
                Log("SCEWIN failed to read EFI vars...");
                Console.Write(SCEWinReadCMD.StandardError.ReadToEnd());
                Console.Write(SCEWinReadCMD.StandardOutput.ReadToEnd());
                Log("Press any key to exit...");
                Console.ReadKey();
                Environment.Exit(1);
            }
        }

        private static void SCEWinWrite()
        {
            SCEWinWriteCMD.Start();
            SCEWinWriteCMD.WaitForExit();
            
            if (SCEWinWriteCMD.ExitCode != 0)
            {
                Log("SCEWIN failed to write EFI vars...");
                Console.Write(SCEWinWriteCMD.StandardError.ReadToEnd());
                Console.Write(SCEWinWriteCMD.StandardOutput.ReadToEnd());
                Log("Press any key to exit...");
                Console.ReadKey();
                Environment.Exit(1);
            }
        }

        private static void ApplyCS(CurveShaperValue CSVal)
        {
            List<string> Lines = File.ReadAllLines("SCEWIN\\nvram.txt").ToList();
            int StartIndex = Lines.FindIndex(x => x.Equals(CSVal.SetupQuestion));
            Lines[StartIndex + 6] = $"Value	=<{CSVal.Value}>";
            File.WriteAllLines("SCEWIN\\nvram.txt", Lines);
        }

        private static int MinutesToMilliseconds(double Minutes)
        {
            return (int)(Minutes * 60 * 1000);
        }

        private static (bool, double) YcruncherThrash1T(int TestRounds, double LastAvgMHz, bool ReducingValue)
        {
            long StartTime;
            double AvgMHz = 0;
            int Iters = 0;
            RyzenEZ.ApplyPBOOffset($"{State.CurrentTestingCore1T}:{State.CurveOptimizerOffsets[State.CurrentTestingCore1T]}");
            // Rounds last thirty seconds
            List<uint> MonitorSensorIndexes = [];
            List<double> NewSensorReadings = [];
            int HighestIndex = 0;

            for (int i = 0; i < SensorIndexes.Count; i++) MonitorSensorIndexes.Add(SensorIndexes[i]["VID"]);
            MonitorSensorIndexes.Add(SensorIndexes[State.CurrentTestingCore1T]["MHz"]);

            for (int i = 0; i < TestRounds; i++)
            {
                StartYcruncher("BKT", $"{State.CurrentTestingCore1T * 2}");
                StartTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                // Allow HWiNFO's effective clock readings to average out
                Thread.Sleep(1000);
                // God I don't want to interpret STDOUT. If MHz is high, we did not crash!
                while (DateTimeOffset.Now.ToUnixTimeMilliseconds() - StartTime <= 55000)
                {
                    Iters += 1;
                    //double NewMHzReading = HWiNFOEZ.GetAvgSensorOverPeriod(SensorIndexes[State.CurrentTestingCore1T]["MHz"], 5000);
                    NewSensorReadings = HWiNFOEZ.GetAvgSensorOverPeriod(MonitorSensorIndexes, 5000);
                    double NewMHzReading = NewSensorReadings[^1];
                    AvgMHz += NewMHzReading;
                    // Now just VIDs
                    NewSensorReadings.RemoveAt(NewSensorReadings.Count - 1);
                    HighestIndex = GetHighestVIDIndex(NewSensorReadings);
                    bool Failed = false;

                    if (NewMHzReading < 4000)
                    {
                        Failed = true;
                        Log("Failure reason: y-cruncher crashed.");
                    }
                    if (AvgMHz / Iters < LastAvgMHz && ReducingValue == true)
                    {
                        Failed = true;
                        Log("Failure reason: Failed to gain any clock speed.");
                    }
                    if (NewSensorReadings[HighestIndex] > NewSensorReadings[State.CurrentTestingCore1T])
                    {
                        Failed = true;
                        Log("Failure reason: Tested core did not maintain the highest average VID request, rendering the test ineffective.");
                    }

                    if (Failed)
                    {
                        ycruncher.Kill(true);
                        AvgMHz /= Iters;
                        Log($"Highest core VID: Core {HighestIndex} at {NewSensorReadings[HighestIndex]}");
                        Log($"Core {State.CurrentTestingCore1T} VID: {NewSensorReadings[State.CurrentTestingCore1T]}");
                        Log($"Previous clock speed: {LastAvgMHz:F2}");
                        Log($"New clock speed: {AvgMHz:F2}");
                        return (true, AvgMHz);
                    }
                }

                ycruncher.Kill(true);
                Thread.Sleep(5000);
            }

            AvgMHz /= Iters;
            Log($"Highest core VID: Core {HighestIndex} at {NewSensorReadings[HighestIndex]}");
            Log($"Core {State.CurrentTestingCore1T} VID: {NewSensorReadings[State.CurrentTestingCore1T]}");
            Log($"Previous clock speed: {LastAvgMHz:F2}");
            Log($"New clock speed: {AvgMHz:F2}");
            return (false, AvgMHz);
        }

        private static void SingleCoreTesting()
        {
            while (State.TotalTestingRounds1T < 3)
            {
                double LastAvgMHz = 0;
                bool ThrashResult;
                State.MoveToNextRound = false;
                if (State.LastShutdownWasClean && State.LoweringValue)
                {   
                    // Run single threaded testing, lowering value until crash...
                    State.LastShutdownWasClean = false;
                    State.LoweringValue = true;
                    Log($"Beginning a single-thread test campaign on core {State.CurrentTestingCore1T}...");
                    while (State.LoweringValue)
                    {
                        (ThrashResult, LastAvgMHz) = YcruncherThrash1T(5, LastAvgMHz, true);

                        if (ThrashResult)
                        {
                            State.LoweringValue = false;
                            State.LastShutdownWasClean = true;
                        }

                        else
                        {
                            Log($"Core {State.CurrentTestingCore1T} succeeded a five minute stress test at Curve Optimizer value {State.CurveOptimizerOffsets[State.CurrentTestingCore1T]}... Lowering by 1.");
                            State.CurveOptimizerOffsets[State.CurrentTestingCore1T] -= 1;
                            State.SaveState();
                        }
                    }
                }

                else
                {   
                    if (!State.LastShutdownWasClean) Log("Recovered from a hard crash!");
                    State.LastShutdownWasClean = false;
                    while (true)
                    {
                        Log($"Core {State.CurrentTestingCore1T} failed at Curve Optimizer value {State.CurveOptimizerOffsets[State.CurrentTestingCore1T]}...");
                        State.CurveOptimizerOffsets[State.CurrentTestingCore1T] += 1;
                        State.SaveState();
                        Log($"Beginning a one hour stress test on core {State.CurrentTestingCore1T} at a raised Curve Optimizer value of {State.CurveOptimizerOffsets[State.CurrentTestingCore1T]}...");
                        (ThrashResult, LastAvgMHz) = YcruncherThrash1T(60, LastAvgMHz, false);
                        if (ThrashResult)
                        {
                            State.CurveOptimizerOffsets[State.CurrentTestingCore1T] += 1;
                            State.SaveState();
                        }

                        else
                        {
                            State.CurrentTestingCore1T += 1;
                            State.LastShutdownWasClean = true;
                            if (State.CurrentTestingCore1T < State.CurveOptimizerOffsets.Count)
                            {
                                Log($"Testing concluded on core {State.CurrentTestingCore1T}, final Curve Optimizer value {State.CurveOptimizerOffsets[State.CurrentTestingCore1T]}");
                                WritePBOOffsetstoEFI();
                            }

                            else
                            {
                                State.MoveToNextRound = true;
                                State.CurrentTestingCore1T = 0;
                            }
                        }
                    }
                }
            }

            // Continue to the next step...
            CleanExit();
        }

        private static void ZeroCurveShaperValues()
        {

            List<string> Lines = File.ReadAllLines("SCEWIN\\nvram.txt").ToList();

            // ??? beyond silly, just let me put in a string regularly!
            int StartIndex = Lines.FindIndex(x => x.Equals("Setup Question	= Min Frequency - Low Temperature"));

            // This *should* be the same for all motherboards, since it's an AGESA option...
            // It is for both an MSI and Asus board that I tested, anyway...
            for (int i = 0; i < 15; i++)
            {
                Lines[StartIndex + (i * 24) + 5] = "Options	=[FF]Auto	// Move \"*\" to the desired Option";
                Lines[StartIndex + (i * 24) + 6] = "         *[01]Enable";
                Lines[StartIndex + (i * 24) + 14] = "Options	=[00]Positive	// Move \"*\" to the desired Option";
                Lines[StartIndex + (i * 24) + 15] = "         *[01]Negative";
            }

            File.WriteAllLines("SCEWIN\\nvram.txt", Lines);
        }

        private static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        private static void SynchronizeVIDs()
        {
            Log($"Detected {SensorIndexes.Count} physical cores on your system.");
            Log("Setting PBO offset to 0 on all cores...");
            RyzenEZ.ZeroPBOOffsets();

            Log("Gathering initial 1T clock speed data...");

            for (int i = 0; i < SensorIndexes.Count; i++)
            {
                StartYcruncher("BKT", $"{i * 2}");
                Thread.Sleep(1000);
                State.StockBKTClockAvg1T.Add(HWiNFOEZ.GetAvgSensorOverPeriod(SensorIndexes[i]["MHz"], 5000));
                ycruncher.Kill(true);
                StartYcruncher("BBP", $"{i * 2}");
                Thread.Sleep(1000);
                State.StockBBPClockAvg1T.Add(HWiNFOEZ.GetAvgSensorOverPeriod(SensorIndexes[i]["MHz"], 5000));
                ycruncher.Kill(true);
            }

            Log("Beginning y-cruncher BKT stress test...");

            string ycruncherAllCore = $"0-{(SensorIndexes.Count * 2) - 1}";

            StartYcruncher("BKT", ycruncherAllCore);

            for (int i = 0; i < SensorIndexes.Count; i++)
            {
                State.CurveOptimizerOffsets.Add(0);
            }

            State.SaveState();

            while (true)
            {   
                uint iterations = 0;
                double[] CoreVIDs = new double[SensorIndexes.Count];
                long StartTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();

                while (DateTimeOffset.Now.ToUnixTimeMilliseconds() - StartTime <= 5000)
                {
                    for (int i = 0; i < SensorIndexes.Count; i++) CoreVIDs[i] += HWiNFOEZ.GetSensorReading(SensorIndexes[i]["VID"]);
                    Thread.Sleep(200);
                    iterations++;
                }

                List<double> CoreAvgVIDs = [];

                for (int i = 0; i < SensorIndexes.Count; i++) CoreAvgVIDs.Add(CoreVIDs[i] / iterations);
                
                // 0.004 is chosen as that is the best-case minimum delta that I have seen.
                if (CoreAvgVIDs.Max() - CoreAvgVIDs.Min() <= 0.004) break;
                
                else
                {
                    int HighestIndex = GetHighestVIDIndex(CoreAvgVIDs);
                    State.CurveOptimizerOffsets[HighestIndex] -= 1;
                    State.SaveState();
                    Log($"Worst VID delta: {CoreAvgVIDs.Max() - CoreAvgVIDs.Min():F3}");
                    RyzenEZ.ApplyPBOOffset($"{HighestIndex}:{State.CurveOptimizerOffsets[HighestIndex]}");
                }

                // Give the values a chance to settle after changing...
                Thread.Sleep(5000);
            }

            Log("VIDs synchronized, gathering initial multithreaded clock speed data...");
            RyzenEZ.ZeroPBOOffsets();
            State.StockBKTClockAvg = HWiNFOEZ.GetAvgSensorOverPeriod(AvgEffectiveClock, 10000);
            State.StockBKTPPTAvg = HWiNFOEZ.GetAvgSensorOverPeriod(PPTIndex, 10000);
            ycruncher.Kill(true);
            StartYcruncher("BBP", ycruncherAllCore);
            State.StockBBPClockAvg = HWiNFOEZ.GetAvgSensorOverPeriod(AvgEffectiveClock, 10000);
            State.StockBBPPPTAvg = HWiNFOEZ.GetAvgSensorOverPeriod(PPTIndex, 10000);
            ycruncher.Kill(true);
            State.SaveState();
            
            for (int i = 0; i < SensorIndexes.Count; i++) RyzenEZ.ApplyPBOOffset($"{i}:{State.CurveOptimizerOffsets[i]}");
            WritePBOOffsetstoEFI();
        }

        private static void WritePBOOffsetstoEFI()
        {
            SCEWinRead();
            List<string> Lines = File.ReadAllLines("SCEWIN\\nvram.txt").ToList();

            int StartIndex = Lines.FindIndex(x => x.Equals("Setup Question	= Core 0 Curve Optimizer Magnitude"));

            for (int i = 0; i < State.CurveOptimizerOffsets.Count; i++)
            {
                Lines[StartIndex + (i * 20) + 5] = $"Value	=<{State.CurveOptimizerOffsets[i]}>";
            }

            File.WriteAllLines("SCEWIN\\nvram.txt", Lines);
        }

        private static void PrintClockImprovement()
        {
            Log($"Original Scalar 1T average: {State.StockPeak1T:F2}");
            Log($"Original AVX2|512 1T average: {State.NewPeak1T:F2}");
            Log($"Original Scalar multicore average: {State.StockBKTClockAvg:F2}");
            Log($"Original AVX2|512 multicore average: {State.StockBBPClockAvg:F2}");
            Log($"New Scalar multicore average: {State.NewBKTClockAvg:F2}");
            Log($"New AVX2|512 multicore average: {State.NewBBPClockAvg:F2}");
            Log($"Improved average multicore clock speed in Scalar workloads by {State.NewBKTClockAvg / State.StockBKTClockAvg:F2}x above baseline.");
            Log($"Improved average multicore clock speed in AVX2|512 workloads by {State.NewBBPClockAvg / State.StockBBPClockAvg:F2}x above baseline.");
            Log($"Original scalar efficiency: {State.StockBKTClockAvg / State.StockBKTPPTAvg:F2} MHz/w");
            Log($"Original AVX2|512 efficiency: {State.StockBBPClockAvg / State.StockBBPPPTAvg:F2} MHz/w");
            Log($"New scalar efficiency: {State.NewBKTClockAvg / State.NewBKTPPTAvg:F2} MHz/w");
            Log($"New AVX2|512 efficiency: {State.NewBBPClockAvg / State.NewBBPPPTAvg:F2} MHz/w");
        }
    }
}

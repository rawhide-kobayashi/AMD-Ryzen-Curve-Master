using System.CodeDom;
using System.Diagnostics;
using System.Security.Principal;
using CurveMaster.include.HWiNFO;
using CurveMaster.include.RyzenUtil;

namespace CurveMaster
{
    class Program
    {
        private static readonly Dictionary<int, Dictionary<string, uint>> SensorIndexes = [];
        private static readonly uint PPTIndex;
        private static readonly uint AvgEffectiveClock;

        static Program()
        {
            if (!IsAdministrator())
            {
                Console.WriteLine("This application must be run as an administrator.");
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
                Environment.Exit(1);
            }

            (SensorIndexes, PPTIndex, AvgEffectiveClock) = HWiNFOEZ.GetSensorIndexes();
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

        private static readonly Process ycruncher = new() {StartInfo = ycruncherInfo};
        private static readonly Process SCEWinRead = new() {StartInfo = SCEWinReadInfo};
        private static readonly Process SCEWinWrite = new() {StartInfo = SCEWinWriteInfo};
        private static readonly Process RebootCMD = new() {StartInfo = RebootCMDInfo};

        private static OCProcessState State = OCProcessState.LoadState();

        private static bool ReadyToReboot = false;

        static int Main()
        {
            while (!ReadyToReboot)
            {
                switch (State.CurrentStep)
                {
                    case "init":
                        Console.WriteLine("Step 0: Collect system information, warn user, and verify capabilities...");
                        Init();
                        State.CurrentStep = "vid_sync";
                        State.SaveState();
                        break;

                    case "vid_sync":
                        Console.WriteLine("Step 1: Synchronizing core VIDs under y-cruncher BKT...");
                        SynchronizeVIDs();
                        State.CurrentStep = "single_core_test_campaign";
                        break;

                    case "single_core_test_campaign":
                        Console.WriteLine("Step 2: Lowering Max Freq - Med Temp Curve Shaper and testing 1T stability...");
                        SingleCoreTesting();
                        break;
                }
            }
            
            ycruncher.Dispose();
            RyzenEZ.Dispose();
            HWiNFOEZ.Dispose();

            return 0;
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
            Console.WriteLine("Welcome to the Ryzen Curve Master!");
            Console.WriteLine("Your computer is about to be sacrificed to the throes of automation; Bluescreens, freezes, and other crashes may occur. Corrupted EFI vars may occur. Continue at your own peril.");
            Console.WriteLine("Please make sure that your current BIOS settings are accurate and correct, and that EFI vars are not password protected.");
            Console.WriteLine("Press any key to continue...");
            Console.ReadKey();
            Console.WriteLine("Testing that SCEWIN can read/write EFI vars...");
            SCEWinRead.Start();
            SCEWinRead.WaitForExit();
            if (SCEWinRead.ExitCode != 0)
            {
                Console.WriteLine("SCEWIN failed to read EFI vars...");
                Console.Write(SCEWinRead.StandardError.ReadToEnd());
                Console.Write(SCEWinRead.StandardOutput.ReadToEnd());
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
                Environment.Exit(1);
            }

            ZeroCurveShaperValues();

            Console.WriteLine("SCEWIN successfully read EFI vars, attempting write...");

            SCEWinWrite.Start();
            SCEWinWrite.WaitForExit();
            
            if (SCEWinWrite.ExitCode != 0)
            {
                Console.WriteLine("SCEWIN failed to write EFI vars...");
                Console.Write(SCEWinWrite.StandardError.ReadToEnd());
                Console.Write(SCEWinWrite.StandardOutput.ReadToEnd());
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
                Environment.Exit(1);
            }

            Console.WriteLine("SCEWIN successfully wrote EFI vars.");
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

        private static void SingleCoreTesting()
        {
            // This should run on the first run of this function
            if (!State.LastRebootChangedCS && State.LastShutdownWasClean && State.MaxF_MedT.Value == 0)
            {
                // Lower curve shaper setting for initial testing and reboot
                State.MaxF_MedT.Value -= 5;
                ApplyCS(State.MaxF_MedT);
                State.LastRebootChangedCS = true;
                State.LastShutdownWasClean = true;
                State.SaveState();
                RebootCMD.Start();
            }

            // This should run if the system had a hard crash during the last testing run
            else if (!State.LastRebootChangedCS && !State.LastShutdownWasClean)
            {
                // Raise curve shaper and reboot
                State.LoweringValue = false;
                State.MaxF_MedT.Value += 1;
                ApplyCS(State.MaxF_MedT);
                State.LastRebootChangedCS = true;
                State.LastShutdownWasClean = true;
                State.SaveState();
                RebootCMD.Start();
            }

            // This should run after a clean value change
            else if (State.LastRebootChangedCS && State.LastShutdownWasClean)
            {
                // Run single threaded testing
                State.LastShutdownWasClean = false;

                if (State.LoweringValue)
                {
                    for (int i = 0; i < State.CurveOptimizerOffsets.Count; i++)
                    {
                        State.LastCoreTested1T = i;
                        StartYcruncher("BKT", $"{i * 2}-{(i * 2) + 1}");
                        Thread.Sleep(MinutesToMilliseconds(5));
                        // God I don't want to interpret STDOUT. If MHz is high, we did not crash!
                        if (HWiNFOEZ.GetAvgSensorOverPeriod(SensorIndexes[i]["MHz"], 5000) > 4000) ycruncher.Kill(true);

                        else
                        {
                            // Raise curve shaper and reboot
                            State.LoweringValue = false;
                            State.MaxF_MedT.Value += 1;
                            ApplyCS(State.MaxF_MedT);
                            State.LastRebootChangedCS = true;
                            State.LastShutdownWasClean = true;
                            State.SaveState();
                            RebootCMD.Start();
                        }
                    }
                }

                else
                {
                    // Assuming that the first core to crash is the worst, when raising the curve, test the last-tested core first...
                    StartYcruncher("BKT", $"{State.LastCoreTested1T * 2}-{(State.LastCoreTested1T * 2) + 1}");
                    Thread.Sleep(MinutesToMilliseconds(5));
                    if (HWiNFOEZ.GetAvgSensorOverPeriod(SensorIndexes[State.LastCoreTested1T]["MHz"], 5000) > 4000)
                    ycruncher.Kill(true);

                    // ...then return to doing a full battery, this time for much, much longer, just to make sure...
                    for (int x = 0; x < 10; x++)
                    {
                        for (int i = 0; i < State.CurveOptimizerOffsets.Count; i++)
                        {
                            State.LastCoreTested1T = i;
                            StartYcruncher("BKT", $"{i * 2}-{(i * 2) + 1}");
                            Thread.Sleep(MinutesToMilliseconds(10));
                            if (HWiNFOEZ.GetAvgSensorOverPeriod(SensorIndexes[i]["MHz"], 5000) > 4000) ycruncher.Kill(true);

                            else
                            {
                                // Raise curve shaper and reboot
                                State.LoweringValue = false;
                                State.MaxF_MedT.Value += 1;
                                ApplyCS(State.MaxF_MedT);
                                State.LastRebootChangedCS = true;
                                State.LastShutdownWasClean = true;
                                State.SaveState();
                                RebootCMD.Start();
                            }
                        }
                    }
                }
            }
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
            Console.WriteLine($"Detected {SensorIndexes.Count} physical cores on your system.");
            Console.WriteLine("Setting PBO offset to 0 on all cores...");
            RyzenEZ.ZeroPBOOffsets();

            Console.WriteLine("Beginning y-cruncher BKT stress test...");

            string ycruncherCoreAffinity = $"0-{(SensorIndexes.Count * 2) - 1}";

            StartYcruncher("BKT", ycruncherCoreAffinity);

            for (int i = 0; i < SensorIndexes.Count; i++)
            {
                State.CurveOptimizerOffsets.Add(0);
            }

            while (true)
            {   
                uint iterations = 0;
                double[] CoreVIDs = new double[SensorIndexes.Count];
                long StartTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();

                while (DateTimeOffset.Now.ToUnixTimeMilliseconds() - StartTime <= 1000)
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
                    Console.WriteLine($"Worst VID delta: {CoreAvgVIDs.Max() - CoreAvgVIDs.Min():F3}");
                    RyzenEZ.ApplyPBOOffset($"{HighestIndex}:{State.CurveOptimizerOffsets[HighestIndex]}");
                }

                // Give the values a chance to settle after changing...
                Thread.Sleep(2000);
            }

            Console.WriteLine("VIDs synchronized, gathering initial clock speed data...");
            RyzenEZ.ZeroPBOOffsets();
            StartYcruncher("BKT", ycruncherCoreAffinity);
            State.StockBKTClockAvg = HWiNFOEZ.GetAvgSensorOverPeriod(AvgEffectiveClock, 10000);
            State.StockBKTPPTAvg = HWiNFOEZ.GetAvgSensorOverPeriod(PPTIndex, 10000);
            ycruncher.Kill(true);
            StartYcruncher("BBP", ycruncherCoreAffinity);
            State.StockBBPClockAvg = HWiNFOEZ.GetAvgSensorOverPeriod(AvgEffectiveClock, 10000);
            State.StockBBPPPTAvg = HWiNFOEZ.GetAvgSensorOverPeriod(PPTIndex, 10000);
            ycruncher.Kill(true);
            
            for (int i = 0; i < SensorIndexes.Count; i++) RyzenEZ.ApplyPBOOffset($"{i}:{State.CurveOptimizerOffsets[i]}");
        }

        private static void PrintClockImprovement()
        {
            Console.WriteLine($"Original Scalar 1T average: {State.StockPeak1T:F2}");
            Console.WriteLine($"Original AVX2|512 1T average: {State.NewPeak1T:F2}");
            Console.WriteLine($"Original Scalar multicore average: {State.StockBKTClockAvg:F2}");
            Console.WriteLine($"Original AVX2|512 multicore average: {State.StockBBPClockAvg:F2}");
            Console.WriteLine($"New Scalar multicore average: {State.NewBKTClockAvg:F2}");
            Console.WriteLine($"New AVX2|512 multicore average: {State.NewBBPClockAvg:F2}");
            Console.WriteLine($"Improved average multicore clock speed in Scalar workloads by {State.NewBKTClockAvg / State.StockBKTClockAvg:F2}x above baseline.");
            Console.WriteLine($"Improved average multicore clock speed in AVX2|512 workloads by {State.NewBBPClockAvg / State.StockBBPClockAvg:F2}x above baseline.");
            Console.WriteLine($"Original scalar efficiency: {State.StockBKTClockAvg / State.StockBKTPPTAvg:F2} MHz/w");
            Console.WriteLine($"Original AVX2|512 efficiency: {State.StockBBPClockAvg / State.StockBBPPPTAvg:F2} MHz/w");
            Console.WriteLine($"New scalar efficiency: {State.NewBKTClockAvg / State.NewBKTPPTAvg:F2} MHz/w");
            Console.WriteLine($"New AVX2|512 efficiency: {State.NewBBPClockAvg / State.NewBBPPPTAvg:F2} MHz/w");
        }
    }
}

using System.Diagnostics;
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

        private static readonly Process ycruncher = new() { StartInfo = ycruncherInfo};
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
        static int Main()
        {
            Console.WriteLine("Welcome to the Ryzen Curve Master!");
            Console.WriteLine("Your computer is about to be sacrificed to the throes of automation; Bluescreens, freezes, and other crashes may occur. Corrupted EFI vars may occur. Continue at your own peril.");
            Console.WriteLine("Please make sure that your current BIOS settings are accurate and correct, and that EFI vars are not password protected.");
            Console.WriteLine("Press any key to continue...");
            Console.ReadKey();
            Console.WriteLine("Testing that SCEWIN can read/write EFI vars...");

            Console.WriteLine("Setting PBO offset to 0 on all cores...");
            RyzenEZ.ZeroPBOOffsets();
            
            Console.WriteLine($"Detected {SensorIndexes.Count} physical cores on your system.");

            List<int> PBOOffsets = [];

            Console.WriteLine("Step 1: Synchronize core VIDs under y-cruncher BKT.");

            ycruncher.Dispose();
            RyzenEZ.Dispose();
            HWiNFOEZ.Dispose();

            return 0;
        }

        private static void StartYcruncher(string testType, int ycruncherThreadCount)
        {
            File.WriteAllText("y-cruncher\\test.cfg", string.Format(ycruncherConfigTemplate, Convert.ToString(ycruncherThreadCount), testType));
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

        private static void SynchronizeVIDs()
        {
            int ycruncherThreadCount = (SensorIndexes.Count * 2) - 1;

            StartYcruncher("BKT", ycruncherThreadCount);

            for (int i = 0; i < SensorIndexes.Count; i++)
            {
                PBOOffsets.Add(0);
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
                    PBOOffsets[HighestIndex] -= 1;
                    Console.WriteLine($"Worst VID delta: {CoreAvgVIDs.Max() - CoreAvgVIDs.Min():F3}");
                    RyzenEZ.ApplyPBOOffset($"{HighestIndex}:{PBOOffsets[HighestIndex]}");
                }

                // Give the values a chance to settle after changing...
                Thread.Sleep(1000);
            }

            Console.WriteLine("Undervolting completed, gathering clock speed data...");

            var NewBKTClockAvg = HWiNFOEZ.GetAvgSensorOverPeriod(AvgEffectiveClock, 10000);
            var NewBKTPPTAvg = HWiNFOEZ.GetAvgSensorOverPeriod(PPTIndex, 10000);

            ycruncher.Kill(true);
            StartYcruncher("BBP", ycruncherThreadCount);

            var NewBBPClockAvg = HWiNFOEZ.GetAvgSensorOverPeriod(AvgEffectiveClock, 10000);
            var NewBBPPPTAvg = HWiNFOEZ.GetAvgSensorOverPeriod(PPTIndex, 10000);

            RyzenEZ.ryzen.SetPsmMarginAllCores(0);

            ycruncher.Kill(true);
            StartYcruncher("BKT", ycruncherThreadCount);

            var StockBKTClockAvg = HWiNFOEZ.GetAvgSensorOverPeriod(AvgEffectiveClock, 10000);
            var StockBKTPPTAvg = HWiNFOEZ.GetAvgSensorOverPeriod(PPTIndex, 10000);

            ycruncher.Kill(true);
            StartYcruncher("BBP", ycruncherThreadCount);

            var StockBBPClockAvg = HWiNFOEZ.GetAvgSensorOverPeriod(AvgEffectiveClock, 10000);
            var StockBBPPTTAvg = HWiNFOEZ.GetAvgSensorOverPeriod(PPTIndex, 10000);

            ycruncher.Kill(true);

            for (int i = 0; i < SensorIndexes.Count; i++) RyzenEZ.ApplyPBOOffset($"{i}:{PBOOffsets[i]}");

            Console.WriteLine($"Original Scalar multicore average: {StockBKTClockAvg:F2}");
            Console.WriteLine($"Original AVX2|512 multicore average: {StockBBPClockAvg:F2}");
            Console.WriteLine($"New Scalar multicore average: {NewBKTClockAvg:F2}");
            Console.WriteLine($"New AVX2|512 multicore average: {NewBBPClockAvg:F2}");

            Console.WriteLine($"Improved average multicore clock speed in Scalar workloads by {NewBKTClockAvg / StockBKTClockAvg:F2}x above baseline.");
            Console.WriteLine($"Improved average multicore clock speed in AVX2|512 workloads by {NewBBPClockAvg / StockBBPClockAvg:F2}x above baseline.");
            Console.WriteLine($"Original scalar efficiency: {StockBKTClockAvg / StockBKTPPTAvg:F2} MHz/w");
            Console.WriteLine($"Original AVX2|512 efficiency: {StockBBPClockAvg / StockBBPPTTAvg:F2} MHz/w");
            Console.WriteLine($"New scalar efficiency: {NewBKTClockAvg / NewBKTPPTAvg:F2} MHz/w");
            Console.WriteLine($"New AVX2|512 efficiency: {NewBBPClockAvg / NewBBPPPTAvg:F2} MHz/w");

            Console.WriteLine("Enter these settings into your BIOS to make this configuration permanent.");
            Console.WriteLine("Final PBO settings:");

            for (int i = 0; i < SensorIndexes.Count; i++) Console.WriteLine($"Core {i}: {PBOOffsets[i]}");

            Console.WriteLine("Thank you for using Ryzen PBO VID Sync.");
            Console.WriteLine("rawhide kobayashi - https://blog.neet.works/");

            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }
    }
}

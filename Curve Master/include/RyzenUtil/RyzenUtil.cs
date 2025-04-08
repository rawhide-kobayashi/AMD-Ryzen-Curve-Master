using System.Management;
using System.Security.Principal;
using System.Text.RegularExpressions;
using ZenStates.Core;

namespace CurveMaster.include.RyzenUtil
{
    public class WmiCmdListItem(string text, uint value, bool isSet = false)
    {
        public uint Value { get; } = value;
        public string Text { get; } = text;

        public bool IsSet { get; } = isSet;

        public override string ToString()
        {
            return this.Text;
        }
    }

    public class RyzenEZ
    {
        public static readonly Cpu Ryzen;
        private static readonly Dictionary<int, int> mappedCores;

        static RyzenEZ()
        {
            if (!IsAdministrator())
            {
                Console.WriteLine("This application must be run as an administrator.");
                Environment.Exit(1);
            }

            try
            {
                Ryzen = new Cpu();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine("If the previous message was unclear, for some reason, ZenStates-Core failed to initialize an instance of the CPU control object.");
                Environment.Exit(1);
            }

            mappedCores = MapLogicalCoresToPhysical();
        }

        private static readonly string wmiAMDACPI = "AMD_ACPI";
        private static readonly string wmiScope = "root\\wmi";
        private static ManagementObject? classInstance;
        private static string? instanceName;
        private static ManagementBaseObject? pack;

        private static List<WmiCmdListItem> availableCommands = [];

        private static bool wmiPopulated = false;
        private static bool rebootFlag = false;

        public static void Dispose()
        {
            Ryzen.Dispose();
        }

        private static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        public static void ApplyPBOOffset(string offsetArgs)
        {
            // This checks if the current SKU has a known register for writing PBO offsets
            if (Ryzen.smu.Rsmu.SMU_MSG_SetDldoPsmMargin != 0)
            {
                string validArgFormat = @"^(-?\d{1,2}(,-?\d{1,2})*|\d{1,2}:-?\d{1,2}(,\d{1,2}:-?\d{1,2})*)$";

                if (Regex.IsMatch(offsetArgs, validArgFormat))
                {
                    string[] arg = offsetArgs.Split(',');

                    if (arg.Length <= mappedCores.Count)
                    {
                        for (int i = 0; i < arg.Length; i++) 
                        {
                            // Magic numbers from SMUDebugTool
                            // This does some bitshifting calculations to get the mask for individual cores for chips with up to two CCDs
                            // Support for threadrippers/epyc is theoretically available, if the calculations were expanded, but are untested
                            try
                            {
                                if (arg[i].Contains(':'))
                                {
                                    int core = Convert.ToInt32(arg[i].Split(':')[0]);
                                    int offset = Convert.ToInt32(arg[i].Split(':')[1]);
                                    int mapIndex = mappedCores[core] < 8 ? 0 : 1;
                                    Ryzen.SetPsmMarginSingleCore((uint)(((mapIndex << 8) | mappedCores[core] % 8 & 0xF) << 20), offset);
                                    Console.WriteLine($"Set logical core {core}, physical core {mappedCores[core]} offset to {offset}!");
                                }

                                else
                                {
                                    int mapIndex = mappedCores[i] < 8 ? 0 : 1;
                                    Ryzen.SetPsmMarginSingleCore((uint)(((mapIndex << 8) | mappedCores[i] % 8 & 0xF) << 20), Convert.ToInt32(arg[i]));
                                    Console.WriteLine($"Set logical core {i}, physical core {mappedCores[i]} offset to {arg[i]}!");
                                }
                            }

                            catch (KeyNotFoundException)
                            {
                                Console.WriteLine($"Tried to set an offset on logical core {Convert.ToInt32(arg[i].Split(':')[0])}, but there are only {mappedCores.Count} (zero-indexed, as a reminder) logical cores active in the system.");
                            }
                        }
                    }

                    else Console.WriteLine("Specified a greater number of offsets than logical cores active in the system. Please check and try again.");
                }

                else Console.WriteLine("Malformed input format for offsets. Please check and try again.");
            }

            else Console.WriteLine("You have attempted to enable PBO offsets on a CPU that does not support them.");
        }

        private static Dictionary<int, int> MapLogicalCoresToPhysical()
        {
            Dictionary<int, int> mappedCores = [];

            int logicalCoreIter = 0;
            
            for (var i = 0; i < Ryzen.info.topology.physicalCores; i++)
            {
                int mapIndex = i < 8 ? 0 : 1;
                if (Ryzen.GetPsmMarginSingleCore((uint)(((mapIndex << 8) | ((i % 8) & 0xF)) << 20)) != null)
                {
                    mappedCores.Add(logicalCoreIter, i);
                    logicalCoreIter += 1;
                }
            }

            return mappedCores;
        }

        public static void ZeroPBOOffsets()
        {
            Ryzen.SetPsmMarginAllCores(0);
        }

        /* public static void TestClockSpeedShit()
        { Ugh
            long last_aperf_eax = 0;
            long last_aperf_edx = 0;
            long new_aperf_eax = 0;
            long new_aperf_edx = 0;
            long last_mperf_eax = 0;
            long last_mperf_edx = 0;
            long new_mperf_eax = 0;
            long new_mperf_edx = 0;

            while (true)
            {
                Thread.Sleep(100);
                ryzen.ReadMsrTx(0xE7, ref new_mperf_eax, ref new_mperf_edx, 5);
                ryzen.ReadMsrTx(0xE8, ref eax_aperf, ref edx_aperf, 5);
                Console.WriteLine((double)eax_aperf / (double)eax_mperf * 4.3);
            }
        } */

        private static string GetWmiInstanceName()
        {
            try
            {
                instanceName = WMI.GetInstanceName(wmiScope, wmiAMDACPI);
            }
            catch
            {
                // ignored
            }

            return instanceName!;
        }

        private static void PopulateWmiFunctions()
        {
            try
            {
                instanceName = GetWmiInstanceName();
                classInstance = new ManagementObject(wmiScope,
                    $"{wmiAMDACPI}.InstanceName='{instanceName}'",
                    null);

                // Get function names with their IDs
                string[] functionObjects = { "GetObjectID", "GetObjectID2" };
                var index = 1;

                foreach (var functionObject in functionObjects)
                {
                    try
                    {
                        pack = WMI.InvokeMethodAndGetValue(classInstance, functionObject, "pack", null, 0);

                        if (pack != null)
                        {
                            var ID = (uint[])pack.GetPropertyValue("ID");
                            var IDString = (string[])pack.GetPropertyValue("IDString");
                            var Length = (byte)pack.GetPropertyValue("Length");

                            for (var i = 0; i < Length; ++i)
                            {
                                if (IDString[i] == "")
                                    break;

                                WmiCmdListItem item = new($"{IDString[i] + ": "}{ID[i]:X8}", ID[i], !IDString[i].StartsWith("Get"));
                                availableCommands.Add(item);
                            }
                        }
                    }
                    catch
                    {
                        // ignored
                    }

                    index++;
                }
            }
            catch
            {
                // ignored
            }

            rebootFlag = true;
            wmiPopulated = true;
        }

        public static void ApplyDisableCores(string coreArgs = "Enable")
        {
            if (!wmiPopulated) PopulateWmiFunctions();

            // More magic from SMUDebugTool...
            // uintccd2 = 0x8200; ? :)
            uint[] ccds = [0x8000, 0x8100];

            var cmdItem = availableCommands.FirstOrDefault(item => item.Text.Contains("Software Downcore Config"));

            if ( cmdItem == null ) {
                Console.Error.WriteLine("Something has gone terribly wrong, the downcore config option is not present.");
                Environment.Exit(7);
            }

            for (int i = 0; i < ccds.Length; i++)
            {
                if (coreArgs != "Enable")
                {
                    int[] arg = [.. coreArgs.Split(',').Select(int.Parse)];

                    for (int x = 0; x < 8; x++)
                    {
                        if (arg.Contains(x + (i * 8)))
                        {
                            ccds[i] = Utils.SetBits(ccds[i], x, 1, 1);
                        }
                    }
                }

                // Unreadable garbage... But it's my unreadable garbage. It just prints the bitmaps in the expected,
                // human order.
                Console.WriteLine($"New core disablement bitmap for CCD{i} (reversed lower half): {new string([.. Convert.ToString((int)(ccds[i] & 0xFF), 2).PadLeft(8, '0').Reverse()])}");
                WMI.RunCommand(classInstance, cmdItem.Value, ccds[i]);
            }
        }
    }
}
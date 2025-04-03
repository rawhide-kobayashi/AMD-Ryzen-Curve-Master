using System.Security.Principal;
using System.Text.RegularExpressions;
using ZenStates.Core;

namespace CurveMaster.include.RyzenUtil
{
    public class RyzenEZ
    {
        public static readonly Cpu ryzen;
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
                ryzen = new Cpu();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine("If the previous message was unclear, for some reason, ZenStates-Core failed to initialize an instance of the CPU control object.");
                Environment.Exit(1);
            }

            mappedCores = MapLogicalCoresToPhysical();
        }

        public static void Dispose()
        {
            ryzen.Dispose();
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
            if (ryzen.smu.Rsmu.SMU_MSG_SetDldoPsmMargin != 0)
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
                                    ryzen.SetPsmMarginSingleCore((uint)(((mapIndex << 8) | mappedCores[core] % 8 & 0xF) << 20), offset);
                                    Console.WriteLine($"Set logical core {core}, physical core {mappedCores[core]} offset to {offset}!");
                                }

                                else
                                {
                                    int mapIndex = mappedCores[i] < 8 ? 0 : 1;
                                    ryzen.SetPsmMarginSingleCore((uint)(((mapIndex << 8) | mappedCores[i] % 8 & 0xF) << 20), Convert.ToInt32(arg[i]));
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
            
            for (var i = 0; i < ryzen.info.topology.physicalCores; i++)
            {
                int mapIndex = i < 8 ? 0 : 1;
                if (ryzen.GetPsmMarginSingleCore((uint)(((mapIndex << 8) | ((i % 8) & 0xF)) << 20)) != null)
                {
                    mappedCores.Add(logicalCoreIter, i);
                    logicalCoreIter += 1;
                }
            }

            return mappedCores;
        }

        public static void ZeroPBOOffsets()
        {
            ryzen.SetPsmMarginAllCores(0);
        }
    }
}
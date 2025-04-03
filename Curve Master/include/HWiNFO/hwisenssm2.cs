// I have no idea what these mean or how to fix them, and it works as it is, so... Get outta my sight!!!
#pragma warning disable CS8605 // Unboxing a possibly null value.
#pragma warning disable CS8602 // Dereference of a possibly null reference.
#pragma warning disable CS8618 // Non-nullable field 'mmf' must contain a non-null value when exiting constructor.

using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

// Many thanks to Martin - https://www.hwinfo.com/forum/threads/shared-memory-interface-v2.1032/

namespace CurveMaster.include.HWiNFO
{
    class HWiNFOWrapper
    {
        public const string HWiNFO_SENSORS_MAP_FILE_NAME2 = "Global\\HWiNFO_SENS_SM2";
        public const string HWiNFO_SENSORS_SM2_MUTEX = "Global\\HWiNFO_SM2_MUTEX";
        public const int HWiNFO_SENSORS_STRING_LEN2 = 128;
        public const int HWiNFO_UNIT_STRING_LEN = 16;

        public enum SENSOR_READING_TYPE
        {
            SENSOR_TYPE_NONE = 0,
            SENSOR_TYPE_TEMP,
            SENSOR_TYPE_VOLT,
            SENSOR_TYPE_FAN,
            SENSOR_TYPE_CURRENT,
            SENSOR_TYPE_POWER,
            SENSOR_TYPE_CLOCK,
            SENSOR_TYPE_USAGE,
            SENSOR_TYPE_OTHER
        };

  
        [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
        public struct _HWiNFO_SENSORS_READING_ELEMENT
        {
            public SENSOR_READING_TYPE tReading;
            public UInt32 dwSensorIndex;
            public UInt32 dwReadingID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = HWiNFO_SENSORS_STRING_LEN2)]
            public string szLabelOrig;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = HWiNFO_SENSORS_STRING_LEN2)]
            public string szLabelUser;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = HWiNFO_UNIT_STRING_LEN)]
            public string szUnit;
            public double Value;
            public double ValueMin;
            public double ValueMax;
            public double ValueAvg;
        };

        [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
        public struct _HWiNFO_SENSORS_SENSOR_ELEMENT
        {
           public UInt32 dwSensorID;
           public UInt32 dwSensorInst;
           [MarshalAs(UnmanagedType.ByValTStr, SizeConst = HWiNFO_SENSORS_STRING_LEN2)]
            public string szSensorNameOrig;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = HWiNFO_SENSORS_STRING_LEN2)]
            public string szSensorNameUser;
        };

        [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
        public struct _HWiNFO_SENSORS_SHARED_MEM2
        {
            public UInt32 dwSignature;
            public UInt32 dwVersion;
            public UInt32 dwRevision;
            public long poll_time;
            public UInt32 dwOffsetOfSensorSection;
            public UInt32 dwSizeOfSensorElement;
            public UInt32 dwNumSensorElements;
            // descriptors for the Readings section
            public UInt32 dwOffsetOfReadingSection; // Offset of the Reading section from beginning of HWiNFO_SENSORS_SHARED_MEM2
            public UInt32 dwSizeOfReadingElement;   // Size of each Reading element = sizeof( HWiNFO_SENSORS_READING_ELEMENT )
            public UInt32 dwNumReadingElements;     // Number of Reading elements
        };
    

	    MemoryMappedFile mmf;
        private uint numSensors;
        private uint numReadingElements;
        private List<string> masterSensorNames;

        private uint offsetSensorSection;
        private uint sizeSensorElement;
        private uint offsetReadingSection;
        private uint sizeReadingSection;

        // ***************************************************************************************************************
        //                                                  Code Example
        // ***************************************************************************************************************

        public void Open()
        {
            masterSensorNames = new List<string>();
            try
            {
                mmf = MemoryMappedFile.OpenExisting(HWiNFO_SENSORS_MAP_FILE_NAME2, MemoryMappedFileRights.Read);
            }
		    catch (Exception Ex)
            {
                Console.WriteLine("An error occured while opening the HWiNFO shared memory! - " + Ex.Message);
                Console.WriteLine("Press ENTER to exit program...");
                Console.ReadLine();
                Environment.Exit(1);
            }
            using (var accessor = mmf.CreateViewAccessor(0, Marshal.SizeOf(typeof(_HWiNFO_SENSORS_SHARED_MEM2)), MemoryMappedFileAccess.Read))
            {
			    _HWiNFO_SENSORS_SHARED_MEM2 HWiNFOMemory ;
                accessor.Read(0, out HWiNFOMemory);
			    numSensors = HWiNFOMemory.dwNumSensorElements ;
                numReadingElements = HWiNFOMemory.dwNumReadingElements;
			    offsetSensorSection = HWiNFOMemory.dwOffsetOfSensorSection ;
                sizeSensorElement = HWiNFOMemory.dwSizeOfSensorElement;
                offsetReadingSection = HWiNFOMemory.dwOffsetOfReadingSection;
                sizeReadingSection = HWiNFOMemory.dwSizeOfReadingElement;

			    for (UInt32 dwSensor = 0; dwSensor < numSensors; dwSensor ++)
                {
                    using (var sensor_element_accessor = mmf.CreateViewStream(offsetSensorSection + (dwSensor * sizeSensorElement), sizeSensorElement, MemoryMappedFileAccess.Read))
                    {
                    
                        byte[] byteBuffer = new byte[sizeSensorElement];
                        sensor_element_accessor.Read(byteBuffer,0,(int)sizeSensorElement);
                        GCHandle handle = GCHandle.Alloc(byteBuffer, GCHandleType.Pinned);
                        _HWiNFO_SENSORS_SENSOR_ELEMENT SensorElement = (_HWiNFO_SENSORS_SENSOR_ELEMENT)Marshal.PtrToStructure(handle.AddrOfPinnedObject(),
                            typeof(_HWiNFO_SENSORS_SENSOR_ELEMENT));
                        handle.Free();

                        masterSensorNames.Add(SensorElement.szSensorNameUser);
                    
					    Console.WriteLine(String.Format("dwSensorID : {0}", SensorElement.dwSensorID));
					    Console.WriteLine(String.Format("dwSensorInst : {0}", SensorElement.dwSensorInst));
					    Console.WriteLine(String.Format("szSensorNameOrig : {0}", SensorElement.szSensorNameOrig));
					    Console.WriteLine(String.Format("szSensorNameUser : {0}", SensorElement.szSensorNameUser));
                    }
                }
                for (UInt32 dwReading = 0; dwReading < numReadingElements; dwReading++)
                {
                    using (var sensor_element_accessor = mmf.CreateViewStream(offsetReadingSection + (dwReading * sizeReadingSection), sizeReadingSection, MemoryMappedFileAccess.Read))
                    {
                        byte[] byteBuffer = new byte[sizeReadingSection];
                        sensor_element_accessor.Read(byteBuffer,0,(int)sizeReadingSection);
                        GCHandle handle = GCHandle.Alloc(byteBuffer, GCHandleType.Pinned);
                        _HWiNFO_SENSORS_READING_ELEMENT ReadingElement = (_HWiNFO_SENSORS_READING_ELEMENT)Marshal.PtrToStructure(handle.AddrOfPinnedObject(),
                            typeof(_HWiNFO_SENSORS_READING_ELEMENT));
                        handle.Free();

                        Console.WriteLine(String.Format("tReading : {0}", ReadingElement.tReading));
                        Console.WriteLine(String.Format("dwSensorIndex : {0} ; Sensor Name: {1}", ReadingElement.dwSensorIndex, masterSensorNames[(int)ReadingElement.dwSensorIndex]));
                        Console.WriteLine(String.Format("dwReadingID : {0}", ReadingElement.dwSensorIndex));
                        Console.WriteLine(String.Format("szLabelUser : {0}", ReadingElement.szLabelUser));
                        Console.WriteLine(String.Format("szUnit : {0}", ReadingElement.szUnit));
                        Console.WriteLine(String.Format("Value : {0}", ReadingElement.Value));
                    }
                }

            }
 	    }
	    public void Close()
        {
            if (mmf != null)
            {
                mmf.Dispose();
            }
        }
    }

    public class HWiNFOEZ
    {
        private static readonly MemoryMappedFile? mmf;

        static HWiNFOEZ()
        {
            try
            {
                mmf = MemoryMappedFile.OpenExisting(HWiNFOWrapper.HWiNFO_SENSORS_MAP_FILE_NAME2, MemoryMappedFileRights.Read);
            }
            catch (Exception Ex)
            {
                Console.WriteLine("An error occured while opening the HWiNFO shared memory! - " + Ex.Message);
                Console.WriteLine("Most likely, it is not enabled. Please make sure it is enabled, and try again.");
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
                Environment.Exit(1);
            }
        }
        public static (Dictionary<int, Dictionary<string, uint>>, uint, uint) GetSensorIndexes()
        {
            uint numSensors;
            uint numReadingElements;
            uint offsetSensorSection;
            uint sizeSensorElement;
            uint offsetReadingSection;
            uint sizeReadingSection;
            List<uint> vid_indexes = [];
            List<uint> mhz_indexes = [];
            List<string> masterSensorNames = [];
            Dictionary<int, Dictionary<string, uint>> SensorIndexes = [];
            uint PPTIndex = 0;
            uint AvgEffectiveClock = 0;

            using (var accessor = mmf.CreateViewAccessor(0, Marshal.SizeOf(typeof(HWiNFOWrapper._HWiNFO_SENSORS_SHARED_MEM2)), MemoryMappedFileAccess.Read))
            {
			    HWiNFOWrapper._HWiNFO_SENSORS_SHARED_MEM2 HWiNFOMemory ;
                accessor.Read(0, out HWiNFOMemory);
			    numSensors = HWiNFOMemory.dwNumSensorElements ;
                numReadingElements = HWiNFOMemory.dwNumReadingElements;
			    offsetSensorSection = HWiNFOMemory.dwOffsetOfSensorSection ;
                sizeSensorElement = HWiNFOMemory.dwSizeOfSensorElement;
                offsetReadingSection = HWiNFOMemory.dwOffsetOfReadingSection;
                sizeReadingSection = HWiNFOMemory.dwSizeOfReadingElement;

			    for (uint dwSensor = 0; dwSensor < numSensors; dwSensor ++)
                {
                    using var sensor_element_accessor = mmf.CreateViewStream(offsetSensorSection + (dwSensor * sizeSensorElement), sizeSensorElement, MemoryMappedFileAccess.Read);
                    byte[] byteBuffer = new byte[sizeSensorElement];
                    sensor_element_accessor.Read(byteBuffer, 0, (int)sizeSensorElement);
                    GCHandle handle = GCHandle.Alloc(byteBuffer, GCHandleType.Pinned);
                    HWiNFOWrapper._HWiNFO_SENSORS_SENSOR_ELEMENT SensorElement = (HWiNFOWrapper._HWiNFO_SENSORS_SENSOR_ELEMENT)Marshal.PtrToStructure(handle.AddrOfPinnedObject(),
                        typeof(HWiNFOWrapper._HWiNFO_SENSORS_SENSOR_ELEMENT));
                    handle.Free();

                    masterSensorNames.Add(SensorElement.szSensorNameUser);
                }
                for (uint dwReading = 0; dwReading < numReadingElements; dwReading++)
                {
                    using var sensor_element_accessor = mmf.CreateViewStream(offsetReadingSection + (dwReading * sizeReadingSection), sizeReadingSection, MemoryMappedFileAccess.Read);
                    byte[] byteBuffer = new byte[sizeReadingSection];
                    sensor_element_accessor.Read(byteBuffer, 0, (int)sizeReadingSection);
                    GCHandle handle = GCHandle.Alloc(byteBuffer, GCHandleType.Pinned);
                    HWiNFOWrapper._HWiNFO_SENSORS_READING_ELEMENT ReadingElement = (HWiNFOWrapper._HWiNFO_SENSORS_READING_ELEMENT)Marshal.PtrToStructure(handle.AddrOfPinnedObject(),
                        typeof(HWiNFOWrapper._HWiNFO_SENSORS_READING_ELEMENT));
                    handle.Free();

                    Match match = Regex.Match(ReadingElement.szLabelOrig, @"(?<core_vid>Core [0-9]* VID)|(?<core_mhz>Core [0-9]* T0 Effective Clock)|(?<ppt>^CPU PPT$)|(?<avg_eff_clock>Average Effective Clock)");

                    if (match.Success)
                    {
                        if (match.Groups["core_vid"].Success) vid_indexes.Add(dwReading);
                        else if (match.Groups["core_mhz"].Success) mhz_indexes.Add(dwReading);
                        else if (match.Groups["ppt"].Success) PPTIndex = dwReading;
                        else if (match.Groups["avg_eff_clock"].Success) AvgEffectiveClock = dwReading;
                    }
                }
            }

            for (int i = 0; i < vid_indexes.Count; i++)
            {
                SensorIndexes.Add(i, new Dictionary<string, uint>());
                SensorIndexes[i]["VID"] = vid_indexes[i];
                SensorIndexes[i]["MHz"] = mhz_indexes[i];
            }

            return (SensorIndexes, PPTIndex, AvgEffectiveClock);
        }

        public static double GetSensorReading(uint index)
        {
            uint offsetReadingSection;
            uint sizeReadingSection;

            using var accessor = mmf.CreateViewAccessor(0, Marshal.SizeOf(typeof(HWiNFOWrapper._HWiNFO_SENSORS_SHARED_MEM2)), MemoryMappedFileAccess.Read);
            HWiNFOWrapper._HWiNFO_SENSORS_SHARED_MEM2 HWiNFOMemory;
            accessor.Read(0, out HWiNFOMemory);
            offsetReadingSection = HWiNFOMemory.dwOffsetOfReadingSection;
            sizeReadingSection = HWiNFOMemory.dwSizeOfReadingElement;

            using var sensor_element_accessor = mmf.CreateViewStream(offsetReadingSection + (index * sizeReadingSection), sizeReadingSection, MemoryMappedFileAccess.Read);
            byte[] byteBuffer = new byte[sizeReadingSection];
            sensor_element_accessor.Read(byteBuffer, 0, (int)sizeReadingSection);
            GCHandle handle = GCHandle.Alloc(byteBuffer, GCHandleType.Pinned);
            HWiNFOWrapper._HWiNFO_SENSORS_READING_ELEMENT ReadingElement = (HWiNFOWrapper._HWiNFO_SENSORS_READING_ELEMENT)Marshal.PtrToStructure(handle.AddrOfPinnedObject(),
                typeof(HWiNFOWrapper._HWiNFO_SENSORS_READING_ELEMENT));
            handle.Free();

            return ReadingElement.Value;
        }

        public static double GetAvgSensorOverPeriod(uint SensorIndex, uint PeriodMS)
        {
            long StartTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            List<double> SensorReadings = [];

            while (DateTimeOffset.Now.ToUnixTimeMilliseconds() - StartTime <= 5000)
            {
                SensorReadings.Add(GetSensorReading(SensorIndex));
                Thread.Sleep(200);
            }

            double SumSensorReadings = 0;

            for (int i = 0; i < SensorReadings.Count; i++)
            {
                SumSensorReadings += SensorReadings[i];
            }

            return SumSensorReadings / SensorReadings.Count;
        }

        public static void Dispose()
        {
            mmf.Dispose();
        }
    }
}

// ***************************************************************************************************************
//                                          HWiNFO Shared Memory Footprint
// ***************************************************************************************************************
//
//         |-----------------------------|-----------------------------------|-----------------------------------|
// Content |  HWiNFO_SENSORS_SHARED_MEM2 |  HWiNFO_SENSORS_SENSOR_ELEMENT[]  | HWiNFO_SENSORS_READING_ELEMENT[]  |
//         |-----------------------------|-----------------------------------|-----------------------------------|
// Pointer |<--0                         |<--dwOffsetOfSensorSection         |<--dwOffsetOfReadingSection        |
//         |-----------------------------|-----------------------------------|-----------------------------------|
// Size    |  dwOffsetOfSensorSection    |   dwSizeOfSensorElement           |    dwSizeOfReadingElement         |
//         |                             |      * dwNumSensorElement         |       * dwNumReadingElement       |
//         |-----------------------------|-----------------------------------|-----------------------------------|
//

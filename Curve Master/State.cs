using Newtonsoft.Json;

namespace CurveMaster
{
    public class CurveShaperValue(string SetupQuestion, int Value)
    {
        public readonly string SetupQuestion = SetupQuestion;
        public int Value = Value;
    }

    public class OCProcessState
    {
        public string CurrentStep = "init";
        public List<int> CurveOptimizerOffsets = [];
        public bool LastShutdownWasClean = true;
        public static readonly string StateFilePath = "state.json";

        // Basic statistics to gauge real improvements
        public double StockBKTClockAvg;
        public double StockBKTPPTAvg;
        public double StockBBPClockAvg;
        public double StockBBPPPTAvg;
        public double StockPeak1T;
        public double NewBKTClockAvg;
        public double NewBKTPPTAvg;
        public double NewBBPClockAvg;
        public double NewBBPPPTAvg;
        public double NewPeak1T;

        // Curve Shaper values BE CAREFUL THERE ARE TAB CHARACTERS IN THERE
        public CurveShaperValue MinF_LowT = new("Setup Question	= Min Frequency - Low Temperature Magnitude", 0);
        public CurveShaperValue MinF_MedT = new("Setup Question	= Min Frequency - Med Temperature Magnitude", 0);
        public CurveShaperValue MinF_HiT = new("Setup Question	= Min Frequency - High Temperature Magnitude", 0);
        public CurveShaperValue LowF_LowT = new("Setup Question	= Low Frequency - Low Temperature Magnitude", 0);
        public CurveShaperValue LowF_MedT = new("Setup Question	= Low Frequency - Med Temperature Magnitude", 0);
        public CurveShaperValue LowF_HiT = new("Setup Question	= Low Frequency - High Temperature Magnitude", 0);
        public CurveShaperValue MedF_LowT = new("Setup Question	= Med Frequency - Low Temperature Magnitude", 0);
        public CurveShaperValue MedF_MedT = new("Setup Question	= Med Frequency - Med Temperature Magnitude", 0);
        public CurveShaperValue MedF_HiT = new("Setup Question	= Med Frequency - High Temperature Magnitude", 0);
        public CurveShaperValue HiF_LowT = new("Setup Question	= High Frequency - Low Temperature Magnitude", 0);
        public CurveShaperValue HiF_MedT = new("Setup Question	= High Frequency - Med Temperature Magnitude", 0);
        public CurveShaperValue HiF_HiT = new("Setup Question	= High Frequency - High Temperature Magnitude", 0);
        public CurveShaperValue MaxF_LowT = new("Setup Question	= Max Frequency - Low Temperature Magnitude", 0);
        public CurveShaperValue MaxF_MedT = new("Setup Question	= Max Frequency - Med Temperature Magnitude", 0);
        public CurveShaperValue MaxF_HiT = new("Setup Question	= Max Frequency - High Temperature Magnitude", 0);

        public int LastCoreTested1T;
        public bool LastRebootChangedCS = false;
        public bool LoweringValue = true;
        

        public void SaveState()
        {
            try
            {
                //string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                string json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(StateFilePath, json);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error saving state: {ex.Message}");
                Environment.Exit(1);
            }
        }

        public static OCProcessState LoadState()
        {
            try
            {
                if (File.Exists(StateFilePath))
                {
                    string json = File.ReadAllText(StateFilePath);
                    return JsonConvert.DeserializeObject<OCProcessState>(json);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error loading state: {ex.Message}");
                Environment.Exit(1);
            }

            return new OCProcessState();
        }
    }
}

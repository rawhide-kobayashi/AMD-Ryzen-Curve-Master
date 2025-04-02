using System;
using System.IO;
using System.Text.Json;

namespace CurveMaster
{
    public class State
    {
        public int CurrentStep { get; set; }
        public List<int> CurveOptimizerOffsets = [];
        public bool LastShutdownWasClean;
        public static string StateFilePath => "state.json";

        public void SaveState()
        {
            try
            {
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(StateFilePath, json);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error saving state: {ex.Message}");
                Environment.Exit(1);
            }
        }

        public static State LoadState()
        {
            try
            {
                if (File.Exists(StateFilePath))
                {
                    string json = File.ReadAllText(StateFilePath);
                    return JsonSerializer.Deserialize<State>(json) ?? new State();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error loading state: {ex.Message}");
                Environment.Exit(1);
            }

            return new State();
        }
    }
}

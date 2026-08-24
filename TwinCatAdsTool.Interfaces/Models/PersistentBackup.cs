using Newtonsoft.Json.Linq;

namespace TwinCatAdsTool.Interfaces.Models
{
    /// <summary>The json produced by a backup together with the outcome of the run that produced it.</summary>
    public class PersistentBackup
    {
        public PersistentBackup(JObject data, PersistentOperationReport report)
        {
            Data = data;
            Report = report;
        }

        public JObject Data { get; }
        public PersistentOperationReport Report { get; }
    }
}

// v1
/**
* On-screen GUI overlay showing placement progress during world generation.
* MonoBehaviour, created/destroyed by GenerationProgress lifecycle methods.
* Reads GenerationProgress static state each OnGUI frame for live updates.
* I was thinking about replacing it with something less CLIish but then
* I woke up realizing that I am doing this for free :P
*/
#nullable disable
using System.Text;
using UnityEngine;

namespace LPA
{
    public class ProgressOverlay : MonoBehaviour
    {
        public static ProgressOverlay instance;
        private GUIStyle _style;
        private Font _valheimFont;
        private readonly string[] _spinner = new string[] { "|", "/", "-", "\\" };//who does not love a good spinner
        private bool _pendingDestroy = false;

        public static void EnsureInstance()
        {
            if (instance == null)
            {
                GameObject go = new GameObject("LPAProgressOverlay");
                DontDestroyOnLoad(go);
                instance = go.AddComponent<ProgressOverlay>();
            }
        }

        public static void DestroyInstance()
        {
            if (instance != null)
            {
                instance._pendingDestroy = true;
                instance = null;
            }
        }

        void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
        }

        void Start()
        {
            Font[] allFonts = Resources.FindObjectsOfTypeAll<Font>();
            for (int i = 0; i < allFonts.Length; i++)
            {
                if (allFonts[i].name == "AveriaSerifLibre-Bold")
                {
                    this._valheimFont = allFonts[i];
                    break;
                }
            }
        }

        void OnGUI()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            if (Event.current.type != EventType.Repaint)
            {
                return;
            }

            bool surveying = GenerationProgress.IsSurveying;
            bool minimapGen = MinimapParallelizer.IsGenerating;

            if (!minimapGen && !surveying && string.IsNullOrEmpty(GenerationProgress.StaticTopText))
            {
                return;
            }

            if (this._style == null)
            {
                this._style = new GUIStyle(GUI.skin.label)
                {
                    richText = true,
                    alignment = TextAnchor.UpperLeft,
                    font = this._valheimFont
                };
            }

            float now = Time.realtimeSinceStartup;
            Rect rect = new Rect(Screen.width - 780, 20, 760, Screen.height - 40);
            int spinIdx = (int)(now * 8f) % this._spinner.Length;

            if (minimapGen)
            {
                float pct = MinimapParallelizer.Progress * 100f;
                string minimapText =
                    $"<color=#FFDD44><size=28><b>Generating minimap: {pct:0.0}%  {this._spinner[spinIdx]}</b></size></color>";
                GUI.Label(rect, minimapText, this._style);
                return;
            }

            if (surveying)
            {
                float pct = WorldSurveyData.SurveyProgress * 100f;
                string surveyText =
                    $"<color=#FFDD44><size=28><b>Surveying the map: {pct:0.0}%  {this._spinner[spinIdx]}</b></size></color>";
                GUI.Label(rect, surveyText, this._style);
                return;
            }

            if (!string.IsNullOrEmpty(GenerationProgress.StaticTopText))
            {
                // Live counter lines rebuilt each frame. The counters are written by worker threads via Interlocked so reading here gives smooth updates.
                int processed = GenerationProgress.CurrentProcessed;
                int placed = GenerationProgress.CurrentPlaced;
                int total = GenerationProgress.TotalRequested;
                float attemptedPct = 0f;
                if (total > 0)
                {
                    attemptedPct = 100f * processed / total;
                }
                float successPct = 0f;
                if (processed > 0)
                {
                    successPct = 100f * placed / processed;
                }
                string liveCounters =
                    $"<size=24>Attempted placements: {processed}/{total} ({attemptedPct:0.00}%)</size>\n" +
                    $"<size=24>Successfully placed: {placed}/{processed} ({successPct:0.00}%)</size>\n";

                string currentLines;
                string[] slots = GenerationProgress.ThreadSlots;
                if (slots != null && slots.Length > 0)
                {
                    StringBuilder sb = new StringBuilder();
                    for (int i = 0; i < slots.Length; i++)
                    {
                        string slotName = System.Threading.Volatile.Read(ref slots[i]);
                        if (string.IsNullOrEmpty(slotName))
                        {
                            continue;
                        }
                        sb.AppendLine($"<size=20>T{i + 1}: {slotName}  {this._spinner[spinIdx]}</size>");
                    }
                    currentLines = sb.ToString();
                }
                else
                {
                    string currentPrefab = "Finished";
                    if (GenerationProgress.CurrentLocation != null)
                    {
                        currentPrefab = GenerationProgress.CurrentLocation.m_prefabName;
                    }
                    currentLines = $"<size=22>Current: {currentPrefab}  {this._spinner[spinIdx]}</size>\n";
                }

                string fullMessage = GenerationProgress.StaticTopText + liveCounters + currentLines + GenerationProgress.StaticBottomText;
                GUI.Label(rect, fullMessage, this._style);
                if (_pendingDestroy)
                {
                    Destroy(gameObject);
                }
            }
        }
    }
}

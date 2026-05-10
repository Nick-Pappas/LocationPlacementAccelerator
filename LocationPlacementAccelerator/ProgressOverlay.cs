// v1.1
/**
* On-screen GUI overlay showing placement progress during world generation.
* MonoBehaviour, created/destroyed by GenerationProgress lifecycle methods.
* Reads GenerationProgress static state each OnGUI frame for live updates.
* I was thinking about replacing it with something less CLIish but then
* I woke up realizing that I am doing this for free :P
*
* v1.1: Two-bug fixes in OnGUI.
*   1) Cursor.lockState/Cursor.visible were being written every OnGUI 
*      invocation regardless of event type or whether anything was being 
*      rendered. While the overlay GameObject was alive, every frame fought
*      vanilla camera/player code's cursor lock. Visible symptom: floating /
*      flickering cursor in-game whenever the overlay GameObject existed but 
*      had nothing to draw (most commonly: a saved-world load that erroneously 
*      regenerates the minimap (EWS issue), then the overlay sticks around because the 
*      destruction path is broken; see #2).
*   2) The Destroy(gameObject) call was nested inside the 
*      `if (!string.IsNullOrEmpty(StaticTopText))` branch. ForceCleanup
*      clears StaticTopText after calling DestroyInstance, so the next 
*      OnGUI tick hit the line-78-ish whatever it was... early return and never reached the 
*      destruction code. GameObject became orphaned, OnGUI kept firing forever,
*      cursor kept getting stomped. Nice stuff...
*      The fix moves the _pendingDestroy check to the very top of OnGUI so destruction is 
*      unconditional once requested.
* The cursor unlock is now scoped to frames where the overlay is actually 
* rendering (which is also when the user should see and read the GUI).
* 
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
            /**
            * Top of function destruction so we cannot leave the GameObject 
            * orphaned in any state. ForceCleanup clears StaticTopText after 
            * marking us for destruction
            */
            if (_pendingDestroy)
            {
                Destroy(gameObject);
                return;
            }

            bool surveying = GenerationProgress.IsSurveying;
            bool minimapGen = MinimapParallelizer.IsGenerating;
            bool hasProgress = !string.IsNullOrEmpty(GenerationProgress.StaticTopText);

            /**
            * Nothing to render. Do NOT touch cursor state in this branch  
            * we have no on-screen UI for the user to interact with, and 
            * unlocking the cursor here would fight vanilla camera/player 
            * code every frame for no benefit. Stay away
            */
            if (!minimapGen && !surveying && !hasProgress)
            {
                return;
            }

            /**
            * From here down we ARE rendering an overlay. Force the cursor 
            * visible/free so the user can see and read it while world 
            * generation is hogging the main thread. 
            */
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            if (Event.current.type != EventType.Repaint)
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

            if (hasProgress)
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
            }
        }
    }
}
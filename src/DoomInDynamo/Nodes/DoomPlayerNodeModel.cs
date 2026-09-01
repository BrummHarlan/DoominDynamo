using System;
using System.Collections.Generic;
using System.Xml;
using Dynamo.Graph;
using Dynamo.Graph.Nodes;
using Newtonsoft.Json;
using ProtoCore.AST.AssociativeAST;

namespace DoomInDynamo.Nodes
{
    /// <summary>
    /// The Dynamo-facing half of the node: registers its one output port and
    /// persists <see cref="WadPath"/> across save/reload. All the actual game
    /// hosting lives in the custom view (see UI/DoomPlayerNodeViewCustomization
    /// and UI/DoomPlayerView) - this class has no dependency on ManagedDoom at all,
    /// so it stays usable (if inert) even in a headless/no-UI Dynamo host.
    /// </summary>
    [NodeName("Doom Player")]
    [NodeCategory("DoomInDynamo")]
    [NodeDescription("Plays Doom on the node face. Pick a WAD file with the Browse "
        + "button, click into the screen to give it keyboard focus, then WASD/arrows "
        + "to move, Alt to fire, Space to use, Esc for the menu.")]
    [IsDesignScriptCompatible]
    public class DoomPlayerNodeModel : NodeModel
    {
        private string wadPath;

        public DoomPlayerNodeModel()
        {
            InPorts.Add(CreatePwadPort());
            OutPorts.Add(CreateStatusPort());
            RegisterAllPorts();

            // Default size on first placement - big enough to actually see the
            // 320x200 screen plus the toolbar, but the user can drag-resize from
            // here (see IsResizable below).
            Width = 360;
            Height = 320;
        }

        /// <summary>
        /// Deserialization path for .dyn (JSON) load. Without this, Json.NET falls
        /// back to the parameterless constructor and then APPENDS the saved ports
        /// into the already-populated InPorts/OutPorts collections - the node
        /// reopens with duplicated ports and any pwad wire rebinds to the duplicate
        /// while evaluation keeps reading index 0, silently dropping the map path.
        /// Same pattern as Dynamo's own CoreNodeModels (Watch, ColorRange).
        /// </summary>
        [JsonConstructor]
        private DoomPlayerNodeModel(IEnumerable<PortModel> inPorts, IEnumerable<PortModel> outPorts)
            : base(inPorts, outPorts)
        {
            if (InPorts.Count == 1)
            {
                // Port names/tooltips survive the .dyn round-trip, but default-value
                // ASTs are not serialized - re-attach it so an unwired pwad port
                // still evaluates to "".
                InPorts[0].DefaultValue = AstFactory.BuildStringNode("");
            }
            else
            {
                // Graph saved by the earlier input-less version of this node (or an
                // unexpected shape): rebuild the input side.
                InPorts.Clear();
                InPorts.Add(CreatePwadPort());
            }

            if (OutPorts.Count != 1)
            {
                OutPorts.Clear();
                OutPorts.Add(CreateStatusPort());
            }
        }

        // The default value keeps the port optional - with nothing wired the VM
        // evaluates the default empty string and the node behaves exactly as it
        // did before this port existed.
        private PortModel CreatePwadPort()
        {
            return new PortModel(PortType.Input, this,
                new PortData("pwad",
                    "Optional path to a PWAD map (e.g. from RevitToWad.Export) loaded on top of the browsed IWAD.",
                    AstFactory.BuildStringNode("")));
        }

        private PortModel CreateStatusPort()
        {
            return new PortModel(PortType.Output, this,
                new PortData("status", "\"running\" once a WAD is loaded and the game loop has started, otherwise \"idle\"."));
        }

        /// <summary>Lets the user drag-resize the node so the Doom screen isn't
        /// stuck at whatever size it happened to start at.</summary>
        public override bool IsResizable => true;

        /// <summary>Path to the .wad the player picked via the node's Browse button.
        /// Nothing is bundled with this package - see the README for where to get one
        /// you're legally entitled to use.</summary>
        public string WadPath
        {
            get => wadPath;
            set
            {
                if (wadPath == value)
                {
                    return;
                }

                wadPath = value;
                OnNodeModified(true);
            }
        }

        /// <summary>Value of the "pwad" input as of the last graph evaluation, delivered
        /// via the DataBridge callback below. Deliberately NOT serialized - it comes from
        /// the graph each run, so persisting a stale copy would only mislead.</summary>
        public string PwadPath { get; private set; } = string.Empty;

        /// <summary>Raised (only) when <see cref="PwadPath"/> actually changes value,
        /// so the view could react to a re-run rewiring the map. Note the DataBridge
        /// invokes its callbacks from the VM's evaluation, not the WPF dispatcher.</summary>
        public event Action PwadPathChanged;

        // A NodeModel can't just read its input port values - inputs only exist as
        // evaluated values inside the DesignScript VM, not on the UI-side model object.
        // VMDataBridge is Dynamo's standard answer (see the NodeModelsEssentials
        // samples): BuildOutputAst emits an extra AST assignment that calls back into
        // DataBridge.BridgeData with the evaluated input, and the callback registered
        // against this node's GUID receives it on the model side each run.
        protected override void OnBuilt()
        {
            base.OnBuilt();
            VMDataBridge.DataBridge.Instance.RegisterCallback(GUID.ToString(), OnDataBridgeCallback);
        }

        public override void Dispose()
        {
            VMDataBridge.DataBridge.Instance.UnregisterCallback(GUID.ToString());
            base.Dispose();
        }

        private void OnDataBridgeCallback(object data)
        {
            var value = data as string ?? string.Empty;
            if (PwadPath == value)
            {
                return;
            }

            PwadPath = value;
            PwadPathChanged?.Invoke();
        }

        public override IEnumerable<AssociativeNode> BuildOutputAst(List<AssociativeNode> inputAstNodes)
        {
            var status = string.IsNullOrEmpty(WadPath) ? "idle" : "running";
            var result = new List<AssociativeNode>
            {
                AstFactory.BuildAssignment(GetAstIdentifierForOutputIndex(0), AstFactory.BuildStringNode(status))
            };

            // Pipe input 0 through the DataBridge (see OnBuilt) so PwadPath tracks
            // whatever the graph evaluates for the "pwad" port. Guarded because Dynamo
            // can call BuildOutputAst with nothing for the input in edge states (e.g.
            // mid-deserialization) - in that case just emit the status assignment.
            if (inputAstNodes != null && inputAstNodes.Count > 0 && inputAstNodes[0] != null)
            {
                result.Add(AstFactory.BuildAssignment(
                    AstFactory.BuildIdentifier(AstIdentifierBase + "_bridge"),
                    VMDataBridge.DataBridge.GenerateBridgeDataAst(GUID.ToString(), inputAstNodes[0])));
            }

            return result;
        }

        // SerializeCore/DeserializeCore are marked obsolete in this Dynamo version -
        // the doc comment on the base member says they're now only exercised for
        // undo/redo and copy/paste, with full .dyn (JSON) persistence handled
        // separately via the public WadPath property. Kept (with the warning
        // suppressed rather than silenced by marking our own method obsolete, which
        // would be misleading) so WadPath still survives undo/redo and copy/paste.
#pragma warning disable CS0672, CS0618
        protected override void SerializeCore(XmlElement element, SaveContext context)
        {
            base.SerializeCore(element, context);
            element.SetAttribute("wadPath", WadPath ?? string.Empty);
        }

        protected override void DeserializeCore(XmlElement nodeElement, SaveContext context)
        {
            base.DeserializeCore(nodeElement, context);
            var attr = nodeElement.Attributes["wadPath"];
            if (attr != null)
            {
                wadPath = attr.Value;
            }
        }
#pragma warning restore CS0672, CS0618
    }
}

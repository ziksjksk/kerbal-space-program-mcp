"jhÆ–•ã•Rw±•Á-y◊ßváﬂäW°j rÍÎy‘·y˙%ñå"û•zgß∂∆´zz-rZ,y‘Ñú0ì9∏ƒú0ì9∏ƒú0ì9∏Œn;äwªÎJh≤+b¢~u"{⁄ñ'N•Í⁄∂*'"w^∆ä^≠´b¢w⁄äWù∂öÆ∂≤äw^≈Î⁄ñÊ≠y€hûÈeusing System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace KspMcp
{
    internal sealed class KspMcpCraft
    {
        private const string IdPrefix = "ksp-mcp-id=";
        private const string CustomDataPrefix = "ksp-mcp-data=";
        private const float EditorRootHeight = 50f;
        private readonly Dictionary<string, Part> _partsById = new Dictionary<string, Part>(StringComparer.Ordinal);
        private readonly Dictionary<Part, string> _idsByPart = new Dictionary<Part, string>();
        private readonly Dictionary<string, int> _requestedStages = new Dictionary<string, int>(StringComparer.Ordinal);
        private string _craftName = "MCP Craft";
        private string _craftDescription = "";
        private string _editorMode = "VAB";
        private uint _fallbackFlightId = 900000000;
        private bool _loadPending;
        private int _loadFrames;
        private int _loadLastPartCount = -1;
        private int _loadStableFrames;
        private int _expectedLoadPartCount;
        private int _jobCounter;
        private BuildJob _buildJob;
        private bool _deferStageRestoration;

        private sealed class BuildJob
        {
            public string Id;
            public List<Dictionary<string, object>> Pending;
            public int Total;
            public int PartsPerFrame;
            public string State;
            public string Error;
            public int Completed;
            public string LastPartId;
            public string LastPartName;
        }

        public bool IsEditorAvailable
        {
            get { return EditorLogic.fetch != null && EditorLogic.fetch.ship != null; }
        }

        public void Tick()
        {
            TickBuildJob();
            if (!_loadPending) return;

            _loadFrames++;
            if (!IsEditorAvailable) return;

            int partCount = EditorLogic.fetch.ship.parts == null ? 0 : EditorLogic.fetch.ship.parts.Count;
            if (partCount > 0 && partCount == _loadLastPartCount) _loadStableFrames++;
            else _loadStableFrames = 0;
            _loadLastPartCount = partCount;

            bool reachedExpectedCount = _expectedLoadPartCount <= 0 || partCount >= _expectedLoadPartCount;
            bool timedOut = _loadFrames >= 300;
            if (partCount <= 0 || (!reachedExpectedCount && !timedOut) || (_loadStableFrames < 5 && !timedOut)) return;

            SyncPartMap();
            RestoreRequestedStages();
            EnsureEditorRootPlacement(true);
            FireEditorModified();
            if (!reachedExpectedCount)
            {
                KspMcpBridge.Log("craft load stabilized with " + partCount + " of " + _expectedLoadPartCount + " expected parts");
            }
            _loadPending = false;
        }

        private void TickBuildJob()
        {
            if (_buildJob == null || (_buildJob.State != "queued" && _buildJob.State != "running")) return;
            if (!IsEditorAvailable) return;

            _buildJob.State = "running";
            int budget = Math.Max(1, Math.Min(16, _buildJob.PartsPerFrame));
            try
            {
                for (int step = 0; step < budget && _buildJob.Pending.Count > 0; step++)
                {
                    bool progress = false;
                    for (int index = _buildJob.Pending.Count - 1; index >= 0; index--)
                    {
                        Dictionary<string, object> part = _buildJob.Pending[index];
                        string parentId = JsonUtil.String(part, "parent_id", null);
                        if (!string.IsNullOrEmpty(parentId) && !_partsById.ContainsKey(parentId)) continue;
                        _deferStageRestoration = true;
                        try
                        {
                            AddPartInternal(part);
                        }
                        finally
                        {
                            _deferStageRestoration = false;
                        }
                        _buildJob.Pending.RemoveAt(index);
                        _buildJob.Completed++;
                        _buildJob.LastPartId = JsonUtil.String(part, "id", null);
                        _buildJob.LastPartName = JsonUtil.String(part, "part", null);
                        KspMcpBridge partBridge = KspMcpBridge.Instance;
                        if (partBridge != null)
                        {
                            partBridge.RecordEvent("editor.build.part_added", new Dictionary<string, object>
                            {
                                { "job_id", _buildJob.Id },
                                { "part_id", _buildJob.LastPartId },
                                { "part", _buildJob.LastPartName },
                                { "completed", _buildJob.Completed },
                                { "total", _buildJob.Total }
                            });
                        }
                        progress = true;
                        break;
                    }
                    if (!progress)
                    {
                        throw new KspMcpException("invalid_craft", "could not resolve parent order; check parent_id values and cycles", null);
                    }
                }

                EnsureEditorRootPlacement();
                FireEditorModified();
                KspMcpBridge progressBridge = KspMcpBridge.Instance;
                if (progressBridge != null)
                {
                    progressBridge.RecordEvent("editor.build.progress", new Dictionary<string, object>
                    {
                        { "job_id", _buildJob.Id },
                        { "completed", _buildJob.Completed },
                        { "total", _buildJob.Total },
                        { "state", _buildJob.Pending.Count == 0 ? "completed" : "running" }
                    });
                }

                if (_buildJob.Pending.Count == 0)
                {
                    RestoreRequestedStages();
                    EnsureEditorRootPlacement(true);
                    FireEditorModified();
                    _buildJob.State = "completed";
                    _buildJob.Completed = _buildJob.Total;
                }
            }
            catch (Exception exception)
            {
                try { ClearInternal(); } catch (Exception) { }
                _buildJob.State = "error";
                _buildJob.Error = exception.Message;
                KspMcpBridge bridge = KspMcpBridge.Instance;
                if (bridge != null)
                {
                    bridge.RecordEvent("editor.build.failed", new Dictionary<string, object>
                    {
                        { "job_id", _buildJob.Id },
                        { "completed", _buildJob.Completed },
                        { "total", _buildJob.Total },
                        { "error", exception.Message }
                    });
                }
            }
        }

        public Dictionary<string, object> Status()
        {
            if (!IsEditorAvailable)
            {
                return new Dictionary<string, object> { { "available", false } };
            }

            SyncPartMap();
            return new Dictionary<string, object>
            {
                { "available", true },
                { "name", _craftName },
                { "description", _craftDescription },
                { "editor_mode", _editorMode },
                { "part_count", EditorLogic.fetch.ship.parts.Count },
                { "connected", SafeAreAllPartsConnected() },
                { "build_job", JobStatus(null) }
            };
        }

        /// <summary>
        /// Cheap editor summary for high-rate telemetry. Status() is kept
        /// detailed for explicit inspection, but it synchronises every part
        /// map and checks connectivity; doing that ten times per second made
        /// live construction needlessly expensive.
        /// </summary>
        public Dictionary<string, object> CompactStatus()
        {
            if (!IsEditorAvailable)
            {
                return new Dictionary<string, object>
                {
                    { "available", false },
                    { "build_job", JobStatus(null) }
                };
            }
            return new Dictionary<string, object>
            {
                { "available", true },
                { "name", _craftName },
                { "editor_mode", _editorMode },
                { "part_count", EditorLogic.fetch.ship.parts == null ? 0 : EditorLogic.fetch.ship.parts.Count },
                { "build_job", JobStatus(null) }
            };
        }

        public Dictionary<string, object> NewCraft(Dictionary<string, object> args)
        {
            EnsureEditor();
            _buildJob = null;
            ClearInternal();
            _craftName = JsonUtil.String(args, "name", "MCP Craft");
            _craftDescription = JsonUtil.String(args, "description", "");
            _editorMode = NormaliseMode(JsonUtil.String(args, "editor_mode", JsonUtil.String(args, "mode", "VAB")));
            SetShipMetadata();
            return Snapshot();
        }

        public Dictionary<string, object> Clear()
        {
            EnsureEditor();
            _buildJob = null;
            ClearInternal();
            return Snapshot();
        }

        public Dictionary<string, object> ApplyCraft(Dictionary<string, object> args)
        {
            EnsureEditor();
            List<object> rawParts = JsonUtil.Array(args, "parts");
            if (rawParts == null) rawParts = new List<object>();

            var pending = new List<Dictionary<string, object>>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < rawParts.Count; index++)
            {
                Dictionary<string, object> part = JsonUtil.Object(rawParts[index]);
                if (part == null) throw new KspMcpException("invalid_craft", "each craft part must be an object", index);
                string id = JsonUtil.RequiredString(part, "id");
                if (!ids.Add(id)) throw new KspMcpException("invalid_craft", "duplicate part id: " + id, null);
                pending.Add(part);
            }

            bool live = JsonUtil.Boolean(args, "live", false);
            if (live) return StartBuildJob(args, pending);

            _buildJob = null;
            ClearInternal();
            _craftName = JsonUtil.String(args, "name", "MCP Craft");
            _craftDescription = JsonUtil.String(args, "description", "");
            _editorMode = NormaliseMode(JsonUtil.String(args, "editor_mode", "VAB"));
            SetShipMetadata();

            try
            {
                while (pending.Count > 0)
                {
                    bool progress = false;
                    for (int index = pending.Count - 1; index >= 0; index--)
                    {
                        Dictionary<string, object> part = pending[index];
                        string parentId = JsonUtil.String(part, "parent_id", null);
                        if (!string.IsNullOrEmpty(parentId) && !_partsById.ContainsKey(parentId)) continue;
                        AddPartInternal(part);
                        pending.RemoveAt(index);
                        progress = true;
                    }
                    if (!progress)
                    {
                        throw new KspMcpException("invalid_craft", "could not resolve parent order; check parent_id values and cycles", null);
                    }
                }

                EnsureEditorRootPlacement(true);
                FireEditorModified();
                return new Dictionary<string, object>
                {
                    { "applied", true },
                    { "part_count", _partsById.Count },
                    { "craft", Snapshot() },
                    { "validation", Validate() }
                };
            }
            catch
            {
                // A failed batch must not leave an apparently usable half-craft.
                ClearInternal();
                throw;
            }
        }

        private Dictionary<string, object> StartBuildJob(Dictionary<string, object> args, List<Dictionary<string, object>> pending)
        {
            _buildJob = null;
            ClearInternal();
            _craftName = JsonUtil.String(args, "name", "MCP Craft");
            _craftDescription = JsonUtil.String(args, "description", "");
            _editorMode = NormaliseMode(JsonUtil.String(args, "editor_mode", "VAB"));
            SetShipMetadata();
            FireEditorModified();

            int partsPerFrame = Math.Max(1, Math.Min(16, JsonUtil.Integer(args, "parts_per_frame", 8)));
            _jobCounter++;
            _buildJob = new BuildJob
            {
                Id = "build-" + _jobCounter,
                Pending = pending,
                Total = pending.Count,
                PartsPerFrame = partsPerFrame,
                State = "queued",
                Error = null,
                Completed = 0,
                LastPartId = null,
                LastPartName = null
            };
            KspMcpBridge bridge = KspMcpBridge.Instance;
            if (bridge != null)
            {
                bridge.RecordEvent("editor.build.started", new Dictionary<string, object>
                {
                    { "job_id", _buildJob.Id },
                    { "total", _buildJob.Total },
                    { "parts_per_frame", _buildJob.PartsPerFrame }
                });
            }
            return JobStatus(new Dictionary<string, object> { { "job_id", _buildJob.Id } });
        }

        public Dictionary<string, object> JobStatus(Dictionary<string, object> args)
        {
            string requested = JsonUtil.String(args, "job_id", null);
            if (_buildJob == null)
            {
                return new Dictionary<string, object>
                {
                    { "available", false },
                    { "state", "idle" }
                };
            }
            if (!string.IsNullOrEmpty(requested) && !string.Equals(requested, _buildJob.Id, StringComparison.Ordinal))
            {
                throw new KspMcpException("job_not_found", "editor job not found: " + requested, null);
            }
            return new Dictionary<string, object>
            {
                { "available", true },
                { "job_id", _buildJob.Id },
                { "state", _buildJob.State },
                { "completed", _buildJob.Completed },
                { "total", _buildJob.Total },
                { "remaining", Math.Max(0, _buildJob.Total - _buildJob.Completed) },
                { "progress", _buildJob.Total == 0 ? 1d : (double)_buildJob.Completed / _buildJob.Total },
                { "parts_per_frame", _buildJob.PartsPerFrame },
                { "last_part_id", _buildJob.LastPartId },
                { "last_part", _buildJob.LastPartName },
                { "error", _buildJob.Error }
            };
        }

        public Dictionary<string, object> CancelJob(Dictionary<string, object> args)
        {
            if (_buildJob == null) return new Dictionary<string, object> { { "cancelled", false }, { "state", "idle" } };
            string requested = JsonUtil.String(args, "job_id", null);
            if (!string.IsNullOrEmpty(requested) && !string.Equals(requested, _buildJob.Id, StringComparison.Ordinal))
            {
                throw new KspMcpException("job_not_found", "editor job not found: " + requested, null);
            }
            bool active = _buildJob.State == "queued" || _buildJob.State == "running";
            if (active)
            {
                _buildJob.Pending.Clear();
                _buildJob.State = "cancelled";
                KspMcpBridge bridge = KspMcpBridge.Instance;
                if (bridge != null) bridge.RecordEvent("editor.build.cancelled", new Dictionary<string, object>
                {
                    { "job_id", _buildJob.Id },
                    { "completed", _buildJob.Completed },
                    { "total", _buildJob.Total }
                });
            }
            return new Dictionary<string, object> { { "cancelled", active }, { "job", JobStatus(null) } };
        }

        public Dictionary<string, object> AddPart(Dictionary<string, object> args)
        {
            EnsureEditor();
            if (_buildJob != null && (_buildJob.State == "queued" || _buildJob.State == "running"))
            {
                throw new KspMcpException("editor_busy", "an asynchronous editor build is still running", JobStatus(null));
            }
            string id = JsonUtil.String(args, "id", null);
            try
            {
                AddPartInternal(args);
                EnsureEditorRootPlacement();
                FireEditorModified();
                return Snapshot();
            }
            catch
            {
                // Incremental calls have the same no-half-part guarantee as a
                // bulk transaction. ApplyCraft() rolls back the whole batch;
                // this path rolls back only the new instance.
                Part created;
                if (!string.IsNullOrEmpty(id) && _partsById.TryGetValue(id, out created))
                {
                    try { EditorLogic.DeletePart(created); } catch (Exception) { try { EditorLogic.fetch.ship.Remove(created); } catch (Exception) { } UnityEngine.Object.Destroy(created.gameObject); }
                    _partsById.Remove(id);
                    _idsByPart.Remove(created);
                }
                if (!string.IsNullOrEmpty(id)) _requestedStages.Remove(id);
                throw;
            }
        }

        private void AddPartInternal(Dictionary<string, object> args)
        {
            string id = JsonUtil.RequiredString(args, "id");
            string partName = JsonUtil.RequiredString(args, "part");
            KspMcpBridge.Log("add begin id=" + id + " part=" + partName);
            // The frame-sliced builder owns the editor for the duration of
            // the job and updates both maps as it creates each part. A full
            // ShipConstruct scan before every part made large rockets
            // quadratic; keep the scan for interactive/incremental calls.
            if (!_deferStageRestoration) SyncPartMap();
            if (_partsById.ContainsKey(id)) throw new KspMcpException("duplicate_part_id", "part id already exists: " + id, null);
            int requestedStage = JsonUtil.Integer(args, "stage", 0);
            if (requestedStage < 0) throw new KspMcpException("invalid_stage", "stage must be non-negative", requestedStage);
            _requestedStages[id] = requestedStage;

            AvailablePart available = ResolveAvailablePart(partName);
            if (available == null || available.partPrefab == null)
            {
                throw new KspMcpException("part_not_found", "loaded KSP part not found: " + partName, null);
            }

            string variant = JsonUtil.String(args, "variant", null);
            PartVariant previousVariant = null;
            bool restoreVariant = false;
            if (!string.IsNullOrEmpty(variant))
            {
                try
                {
                    PartVariant selected = available.GetVariant(variant);
                    if (selected != null)
                    {
                        previousVariant = available.variant;
                        available.variant = selected;
                        restoreVariant = true;
                    }
                }
                catch (Exception exception)
                {
                    KspMcpBridge.Log("variant " + variant + " could not be applied to " + partName + ": " + exception.Message);
                }
            }

            Part instance;
            try
            {
                KspMcpBridge.Log("add spawn begin id=" + id);
                instance = SpawnEditorPart(available);
                KspMcpBridge.Log("add spawn returned id=" + id + " null=" + (instance == null));
            }
            finally
            {
                if (restoreVariant) available.variant = previousVariant;
            }
            if (instance == null) throw new KspMcpException("part_spawn_failed", "could not instantiate part: " + partName, null);

            instance.partInfo = available;
            KspMcpBridge.Log("add metadata begin id=" + id);
            instance.gameObject.name = "KspMcp_" + id;
            instance.transform.position = JsonUtil.Vector3(args, "position", Vector3.zero);
            instance.transform.rotation = JsonUtil.Quaternion(args, "rotation", Quaternion.identity);
            instance.gameObject.SetActive(true);
            instance.flightID = NextFlightId();
            instance.customPartData = BuildCustomPartData(id, JsonUtil.Get(args, "custom_data"));
            KspMcpBridge.Log("add metadata done id=" + id);

            // Keep the factory result detached from ShipConstruct until the
            // parent relationship is known. For a child, the editor's native
            // attachPart path adds it to the construct and wires the hierarchy
            // in the same order as the stock VAB. Roots are added below.
            _partsById[id] = instance;
            _idsByPart[instance] = id;

            string parentId = JsonUtil.String(args, "parent_id", null);
            if (!string.IsNullOrEmpty(parentId))
            {
                KspMcpBridge.Log("add attach begin id=" + id + " parent=" + parentId);
                Part parent = FindPart(parentId);
                if (parent == null) throw new KspMcpException("parent_not_found", "parent part not found: " + parentId, null);
                string parentNodeId = JsonUtil.String(args, "parent_attach_node", null);
                string childNodeId = JsonUtil.String(args, "attach_node", null);
                if (string.IsNullOrEmpty(parentNodeId) || string.IsNullOrEmpty(childNodeId))
                {
                    throw new KspMcpException("attachment_required", "parented parts require parent_attach_node and attach_node", id);
                }
                AttachInternal(instance, parent, parentNodeId, childNodeId, JsonUtil.Boolean(args, "snap_to_node", true));
                KspMcpBridge.Log("add attach done id=" + id);
            }
            else
            {
                EditorLogic.fetch.ship.Add(instance);
                instance.setParent(null);
                instance.SetHierarchyRoot(instance);
            }

            SetStageInternal(instance, requestedStage);
            if (!_deferStageRestoration) RestoreRequestedStages();
            KspMcpBridge.Log("add stage done id=" + id);
            ApplyActionGroups(instance, args);
            return;
        }

        private static Part SpawnEditorPart(AvailablePart available)
        {
            if (EditorLogic.fetch == null || EditorLogic.fetch.ship == null) return null;

            // EditorLogic.SpawnPart is intended for the interactive part picker
            // and can wait on editor mouse/selection state when called from a
            // non-UI command. KSP 1.12's inventory controller exposes the
            // current, non-obsolete factory used when an editor part is created
            // from the inventory. It initializes Part/PartModule state before
            // the object is added to ShipConstruct, which is important for
            // resource tanks and engines.
            try
            {
                UIPartActionControllerInventory inventory = UIPartActionControllerInventory.Instance;
                if (inventory != null)
                {
                    KspMcpBridge.Log("spawn inventory factory begin part=" + available.name);
                    Part loaded = inventory.CreatePartFromInventory(available);
                    KspMcpBridge.Log("spawn inventory factory returned part=" + available.name + " null=" + (loaded == null));
                    if (loaded != null) return loaded;
                }
            }
            catch (Exception exception)
            {
                KspMcpBridge.Log("inventory factory failed for " + available.name + ": " + exception.GetType().FullName + ": " + exception.Message);
            }

            // Keep the older public wrapper as a compatibility fallback for
            // KSP installations where the inventory controller is unavailable.
            try
            {
                if (EditorLogic.fetch != null)
                {
                    KspMcpBridge.Log("spawn editor factory begin part=" + available.name);
                    Part loaded = EditorLogic.fetch.CreatePartForInventoryUse(available);
                    KspMcpBridge.Log("spawn editor factory returned part=" + available.name + " null=" + (loaded == null));
                    if (loaded != null) return loaded;
                }
            }
            catch (Exception exception)
            {
                KspMcpBridge.Log("editor factory failed for " + available.name + ": " + exception.GetType().FullName + ": " + exception.Message);
            }

            try
            {
                KspMcpBridge.Log("spawn prefab fallback begin part=" + available.name);
                Part fallback = UnityEngine.Object.Instantiate(available.partPrefab) as Part;
                KspMcpBridge.Log("spawn prefab fallback returned part=" + available.name + " null=" + (fallback == null));
                if (fallback != null) return fallback;
            }
            catch (Exception exception)
            {
                KspMcpBridge.Log("prefab fallback failed for " + available.name + ": " + exception.GetType().FullName + ": " + exception.Message);
            }
            return null;
        }

        public Dictionary<string, object> AttachPart(Dictionary<string, object> args)
        {
            EnsureEditor();
            Part child = FindPart(JsonUtil.RequiredString(args, "id"));
            Part parent = FindPart(JsonUtil.RequiredString(args, "parent_id"));
            if (child == null) throw new KspMcpException("part_not_found", "part not found: " + JsonUtil.String(args, "id", ""), null);
            if (parent == null) throw new KspMcpException("parent_not_found", "parent not found: " + JsonUtil.String(args, "parent_id", ""), null);
            DetachInternal(child);
            AttachInternal(
                child,
                parent,
                JsonUtil.RequiredString(args, "parent_attach_node"),
                JsonUtil.RequiredString(args, "attach_node"),
                JsonUtil.Boolean(args, "snap_to_node", true));
            FireEditorModified();
            return Snapshot();
        }

        private void AttachInternal(Part child, Part parent, string parentNodeId, string childNodeId, bool snapToNode)
        {
            if (child == parent) throw new KspMcpException("invalid_attachment", "a part cannot attach to itself", null);
            Part cursor = parent;
            while (cursor != null)
            {
                if (cursor == child) throw new KspMcpException("invalid_attachment", "attachment would create a parent cycle", null);
                cursor = cursor.parent;
            }
            AttachNode parentNode = parent.FindAttachNode(parentNodeId);
            AttachNode childNode = child.FindAttachNode(childNodeId);
            if (parentNode == null) throw new KspMcpException("node_not_found", "parent attachment node not found: " + parentNodeId, null);
            if (childNode == null) throw new KspMcpException("node_not_found", "child attachment node not found: " + childNodeId, null);
            if (parentNode.attachedPart != null && parentNode.attachedPart != child)
            {
                throw new KspMcpException("node_occupied", "parent attachment node is already occupied: " + parentNodeId, null);
            }
            if (childNode.attachedPart != null && childNode.attachedPart != parent)
            {
                throw new KspMcpException("node_occupied", "child attachment node is already occupied: " + childNodeId, null);
            }

            bool alreadyInShip = EditorLogic.fetch != null && EditorLogic.fetch.ship != null && EditorLogic.fetch.ship.Contains(child);
            if (!alreadyInShip)
            {
                // This is the same path the stock editor uses after its
                // checkAttach() result is accepted. In particular, the
                // callerPartNode belongs to the child and otherPartNode to
                // the existing parent; swapping them creates a two-way
                // parent cycle in Part's native hierarchy.
                MethodInfo attachMethod = typeof(EditorLogic).GetMethod(
                    "attachPart",
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(Part), typeof(EditorLogic).Assembly.GetType("Attachment", true, false) },
                    null);
                if (attachMethod == null) throw new KspMcpException("attach_unavailable", "KSP editor attachPart method is unavailable", null);
                Type attachmentType = attachMethod.GetParameters()[1].ParameterType;
                object attachment = Activator.CreateInstance(attachmentType, true);
                SetAttachmentField(attachment, "caller", child);
                SetAttachmentField(attachment, "potentialParent", parent);
                SetAttachmentField(attachment, "callerPartNode", childNode);
                SetAttachmentField(attachment, "otherPartNode", parentNode);
                SetAttachmentField(attachment, "mode", Enum.ToObject(attachmentType.GetField("mode", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).FieldType, 0));
                SetAttachmentField(attachment, "possible", true);
                SetAttachmentField(attachment, "collision", false);
                SetAttachmentField(attachment, "position", child.transform.position);
                SetAttachmentField(attachment, "rotation", child.transform.rotation);
                if (snapToNode) SnapToAttachmentNodes(child, parent, childNode, parentNode);
                try
                {
                    attachMethod.Invoke(EditorLogic.fetch, new object[] { child, attachment });
                }
                catch (TargetInvocationException exception)
                {
                    throw exception.InnerException ?? exception;
                }
                return;
            }

            // Reattaching an existing part cannot use attachPart directly:
            // that private editor method also calls addToShip(), which would
            // add a duplicate entry. Mirror only its native relationship
            // sequence for this already-registered part.
            child.setParent(parent);
            if (snapToNode) SnapToAttachmentNodes(child, parent, childNode, parentNode);
            child.transform.parent = parent.transform;
            childNode.attachedPart = parent;
            parentNode.attachedPart = child;
            child.onAttach(parent, true);
        }

        private static void SetAttachmentField(object attachment, string name, object value)
        {
            FieldInfo field = attachment.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null) throw new KspMcpException("attach_unavailable", "KSP Attachment field is unavailable: " + name, null);
            field.SetValue(attachment, value);
        }

        private static void SnapToAttachmentNodes(Part child, Part parent, AttachNode childNode, AttachNode parentNode)
        {
            if (child == null || parent == null || childNode == null || parentNode == null) return;

            Vector3 parentWorldPosition = parent.transform.TransformPoint(parentNode.position);
            Vector3 childDirection = child.transform.TransformDirection(childNode.orientation);
            Vector3 parentDirection = parent.transform.TransformDirection(parentNode.orientation);
            if (childDirection.sqrMagnitude > 0.000001f && parentDirection.sqrMagnitude > 0.000001f)
            {
                child.transform.rotation = Quaternion.FromToRotation(childDirection, -parentDirection) * child.transform.rotation;

                // Use the node's secondary axis to choose a deterministic roll
                // after matching the primary attach direction. This keeps
                // rotated and surface-attached parts from twisting randomly.
                Vector3 childSecondary = child.transform.TransformDirection(childNode.secondaryAxis);
                Vector3 parentSecondary = parent.transform.TransformDirection(parentNode.secondaryAxis);
                Vector3 targetSecondary = Vector3.ProjectOnPlane(parentSecondary, parentDirection);
                Vector3 currentSecondary = Vector3.ProjectOnPlane(childSecondary, parentDirection);
                if (targetSecondary.sqrMagnitude > 0.000001f && currentSecondary.sqrMagnitude > 0.000001f)
                {
                    child.transform.rotation = Quaternion.FromToRotation(currentSecondary, targetSecondary) * child.transform.rotation;
                }
            }

            Vector3 childWorldPosition = child.transform.TransformPoint(childNode.position);
            child.transform.position += parentWorldPosition - childWorldPosition;
        }

        private void DetachInternal(Part child)
        {
            if (child == null || child.parent == null) return;
            Part parent = child.parent;
            AttachNode node = parent.FindAttachNodeByPart(child);
            if (node != null) node.attachedPart = null;
            if (child.attachNodes != null)
            {
                foreach (AttachNode childNode in child.attachNodes)
                {
                    if (childNode != null && childNode.attachedPart == parent) childNode.attachedPart = null;
                }
            }
            child.onDetach(true);
            child.setParent(null);
            child.SetHierarchyRoot(child);
        }

        public Dictionary<string, object> UpdatePart(Dictionary<string, object> args)
        {
            EnsureEditor();
            Part part = FindPart(JsonUtil.RequiredString(args, "id"));
            if (part == null) throw new KspMcpException("part_not_found", "part not found", null);
            if (JsonUtil.Has(args, "position")) part.transform.position = JsonUtil.Vector3(args, "position", part.transform.position);
            if (JsonUtil.Has(args, "rotation")) part.transform.rotation = JsonUtil.Quaternion(args, "rotation", part.transform.rotation);

            if (args.ContainsKey("parent_id"))
            {
                string parentId = JsonUtil.String(args, "parent_id", null);
                DetachInternal(part);
                if (!string.IsNullOrEmpty(parentId))
                {
                    Part parent = FindPart(parentId);
                    if (parent == null) throw new KspMcpException("parent_not_found", "parent not found: " + parentId, null);
                    AttachInternal(part, parent, JsonUtil.RequiredString(args, "parent_attach_node"), JsonUtil.RequiredString(args, "attach_node"), JsonUtil.Boolean(args, "snap_to_node", true));
                }
            }
            if (JsonUtil.Has(args, "stage"))
            {
                int stage = JsonUtil.Integer(args, "stage", 0);
                SetStageInternal(part, stage);
                _requestedStages[PartId(part)] = stage;
            }
            ApplyActionGroups(part, args);
            if (JsonUtil.Has(args, "custom_data")) part.customPartData = BuildCustomPartData(PartId(part), JsonUtil.Get(args, "custom_data"));
            EnsureEditorRootPlacement(true);
            FireEditorModified();
            return Snapshot();
        }

        public Dictionary<string, object> RemovePart(Dictionary<string, object> args)
        {
            EnsureEditor();
            string partId = JsonUtil.RequiredString(args, "id");
            Part part = FindPart(partId);
            if (part == null) throw new KspMcpException("part_not_found", "part not found", null);
            bool includeChildren = JsonUtil.Boolean(args, "include_children", true);
            if (!includeChildren && part.children != null && part.children.Count > 0)
            {
                throw new KspMcpException("children_present", "refusing to remove a part with children when include_children=false", null);
            }

            try
            {
                EditorLogic.DeletePart(part);
            }
            catch (Exception)
            {
                if (EditorLogic.fetch.ship.Contains(part)) EditorLogic.fetch.ship.Remove(part);
                UnityEngine.Object.Destroy(part.gameObject);
            }
            SyncPartMap();
            _requestedStages.Remove(partId);
            FireEditorModified();
            return Snapshot();
        }

        public Dictionary<string, object> SetStage(Dictionary<string, object> args)
        {
            EnsureEditor();
            Part part = FindPart(JsonUtil.RequiredString(args, "id"));
            if (part == null) throw new KspMcpException("part_not_found", "part not found", null);
            int stage = JsonUtil.Integer(args, "stage", 0);
            SetStageInternal(part, stage);
            _requestedStages[PartId(part)] = stage;
            FireEditorModified();
            return Snapshot();
        }

        private static void SetStageInternal(Part part, int stage)
        {
            if (stage < 0) throw new KspMcpException("invalid_stage", "stage must be non-negative", stage);
            part.inverseStage = stage;
            part.originalStage = stage;
            part.defaultInverseStage = stage;
        }

        private void RestoreRequestedStages()
        {
            if (!IsEditorAvailable) return;
            foreach (Part part in EditorLogic.fetch.ship.parts)
            {
                if (part == null) continue;
                string id = PartId(part);
                int stage;
                if (_requestedStages.TryGetValue(id, out stage))
                {
                    SetStageInternal(part, stage);
                }
                else if (part.inverseStage < 0)
                {
                    SetStageInternal(part, 0);
                }
            }
        }

        public Dictionary<string, object> SetActionGroup(Dictionary<string, object> args)
        {
            EnsureEditor();
            Part part = FindPart(JsonUtil.RequiredString(args, "id"));
            if (part == null) throw new KspMcpException("part_not_found", "part not found", null);
            string action = JsonUtil.RequiredString(args, "action");
            string group = JsonUtil.RequiredString(args, "group");
            int count = SetActionGroupInternal(part, action, group);
            if (count == 0) throw new KspMcpException("action_not_found", "action not found on part: " + action, null);
            FireEditorModified();
            return Snapshot();
        }

        private static int SetActionGroupInternal(Part part, string actionName, string groupName)
        {
            KSPActionGroup group;
            try
            {
                group = (KSPActionGroup)Enum.Parse(typeof(KSPActionGroup), groupName, true);
            }
            catch (Exception exception)
            {
                throw new KspMcpException("invalid_action_group", "unknown KSP action group: " + groupName, exception.Message);
            }

            int changed = 0;
            if (part.Actions == null) return 0;
            foreach (BaseAction action in part.Actions)
            {
                if (action == null) continue;
                bool matches = actionName == "*" || string.Equals(action.name, actionName, StringComparison.OrdinalIgnoreCase) || string.Equals(action.guiName, actionName, StringComparison.OrdinalIgnoreCase);
                if (!matches) continue;
                action.actionGroup = group;
                changed++;
            }
            return changed;
        }

        private static void ApplyActionGroups(Part part, Dictionary<string, object> args)
        {
            Dictionary<string, object> groups = JsonUtil.Object(JsonUtil.Get(args, "action_groups"));
            if (groups == null) return;
            foreach (KeyValuePair<string, object> item in groups)
            {
                if (item.Value is string) SetActionGroupInternal(part, item.Key, (string)item.Value);
            }
        }

        public Dictionary<string, object> Validate()
        {
            EnsureEditor();
            SyncPartMap();
            var errors = new List<object>();
            var warnings = new List<object>();
            int roots = 0;
            int engines = 0;
            int commandModules = 0;
            int decouplers = 0;
            double totalMass = 0d;
            double totalPropellantMass = 0d;
            var stagePartCounts = new Dictionary<int, int>();
            var stageEngineCounts = new Dictionary<int, int>();
            var stageCommandCounts = new Dictionary<int, int>();
            var stageDecouplerCounts = new Dictionary<int, int>();

            foreach (Part part in EditorLogic.fetch.ship.parts)
            {
                if (part == null) continue;
                if (part.parent == null) roots++;
                totalMass += part.mass;
                try { totalMass += part.GetResourceMass(); } catch (Exception) { }
                totalPropellantMass += SafePropellantMass(part);

                bool hasStagedActionModule = false;
                bool hasCommandModule = part.isControllable;

                int partStage = part.inverseStage < 0 ? 0 : part.inverseStage;
                if (!stagePartCounts.ContainsKey(partStage)) stagePartCounts[partStage] = 0;
                if (!stageEngineCounts.ContainsKey(partStage)) stageEngineCounts[partStage] = 0;
                if (!stageCommandCounts.ContainsKey(partStage)) stageCommandCounts[partStage] = 0;
                if (!stageDecouplerCounts.ContainsKey(partStage)) stageDecouplerCounts[partStage] = 0;
                stagePartCounts[partStage]++;

                if (part.Modules != null)
                {
                    foreach (PartModule module in part.Modules)
                    {
                        string moduleName = ModuleName(module);
                        if (moduleName.IndexOf("ModuleEngines", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            engines++;
                            stageEngineCounts[partStage]++;
                            hasStagedActionModule = true;
                        }
                        if (moduleName.IndexOf("Decouple", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            moduleName.IndexOf("Separator", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            moduleName.IndexOf("Seperator", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            decouplers++;
                            stageDecouplerCounts[partStage]++;
                            hasStagedActionModule = true;
                        }
                        if (moduleName.IndexOf("Command", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            hasCommandModule = true;
                        }
                    }
                }

                // A controllable part can expose both isControllable and a
                // ModuleCommand module. Count the physical control part once;
                // double-counting made the no-visual validator report a
                // healthier control architecture than the craft really had.
                if (hasCommandModule)
                {
                    commandModules++;
                    stageCommandCounts[partStage]++;
                }

                if (part.parent != null && part.parent.FindAttachNodeByPart(part) == null)
                {
                    warnings.Add("part " + PartId(part) + " has a parent reference but no discoverable attachment node");
                }
                // KSP legitimately stores passive parts such as command pods,
                // adapters, and tanks with inverseStage == -1.  Only parts
                // that actually perform a staged action must have a stage.
                if (part.inverseStage < 0 && hasStagedActionModule)
                {
                    errors.Add("part " + PartId(part) + " has a negative stage");
                }
            }

            if (EditorLogic.fetch.ship.parts.Count == 0) errors.Add("craft has no parts");
            if (roots != 1 && EditorLogic.fetch.ship.parts.Count > 0) errors.Add("craft has " + roots + " root parts; a rocket must have exactly one connected root");
            if (!SafeAreAllPartsConnected()) errors.Add("KSP reports that not all parts are connected");
            if (commandModules == 0) errors.Add("craft has no controllable command module");
            if (engines == 0) warnings.Add("craft has no engine module");
            else if (totalPropellantMass <= 0d) errors.Add("craft has engines but no usable propellant resources");
            if (decouplers == 0) warnings.Add("craft has no decoupler/separator; this may be intentional");

            var stageSummary = new List<object>();
            var stageNumbers = new List<int>(stagePartCounts.Keys);
            stageNumbers.Sort();
            foreach (int stage in stageNumbers)
            {
                int stageEngines = stageEngineCounts[stage];
                int stageDecouplers = stageDecouplerCounts[stage];
                stageSummary.Add(new Dictionary<string, object>
                {
                    { "stage", stage },
                    { "part_count", stagePartCounts[stage] },
                    { "engine_count", stageEngines },
                    { "command_module_count", stageCommandCounts[stage] },
                    { "decoupler_count", stageDecouplers }
                });

                // Stage 0 is allowed to be a final payload/decoupling step.
                // Any earlier separation stage must also have a continuing
                // engine in the same stage group; otherwise the active craft
                // becomes a controllable but propulsion-less payload.
                if (stage > 0 && stageDecouplers > 0 && stageEngines == 0)
                {
                    errors.Add("stage " + stage + " has a decoupler but no engine; add an upper-stage engine to the same staging group");
                }
            }

            float dryCost = 0f;
            float fuelCost = 0f;
            try { EditorLogic.fetch.ship.GetShipCosts(out dryCost, out fuelCost); }
            catch (Exception exception) { warnings.Add("could not calculate craft cost: " + exception.Message); }

            return new Dictionary<string, object>
            {
                { "valid", errors.Count == 0 },
                { "errors", errors },
                { "warnings", warnings },
                { "summary", new Dictionary<string, object>
                    {
                        { "part_count", EditorLogic.fetch.ship.parts.Count },
                        { "root_count", roots },
                        { "engine_count", engines },
                        { "command_module_count", commandModules },
                        { "decoupler_count", decouplers },
                        { "stage_summary", stageSummary },
                        { "mass_tonnes", totalMass },
                        { "propellant_mass_tonnes", totalPropellantMass },
                        { "dry_cost", dryCost },
                        { "fuel_cost", fuelCost },
                        { "connected", SafeAreAllPartsConnected() }
                    }
                }
            };
        }

        /// <summary>
        /// Calculate launch-facing performance from the parts that KSP has
        /// actually loaded. Values are estimates because fuel crossfeed,
        /// throttle curves and atmospheric drag are dynamic, but this catches
        /// the common failure modes that a visual-free builder cannot see:
        /// insufficient liftoff TWR, an inverted thrust/COM relationship,
        /// missing engines, and staging with no usable propellant.
        /// </summary>
        public Dictionary<string, object> Analyze(Dictionary<string, object> args)
        {
            EnsureEditor();
            SyncPartMap();
            bool includeParts = JsonUtil.Boolean(args, "include_parts", false);
            const double Gravity = 9.80665d;

            Vector3 centerMassSum = Vector3.zero;
            Vector3 centerThrustSum = Vector3.zero;
            double totalMass = 0d;
            double totalPropellantMass = 0d;
            double totalSeaLevelThrust = 0d;
            double totalVacuumThrust = 0d;
            double totalSeaIspThrust = 0d;
            double totalVacuumIspThrust = 0d;
            int engineCount = 0;
            var stageData = new Dictionary<int, Dictionary<string, object>>();
            var engineReports = new List<object>();

            foreach (Part part in EditorLogic.fetch.ship.parts)
            {
                if (part == null) continue;
                double partMass = SafePartMass(part);
                double partPropellant = SafePropellantMass(part);
                totalMass += partMass;
                totalPropellantMass += partPropellant;
                centerMassSum += part.transform.position * (float)partMass;

                int stage = part.inverseStage < 0 ? 0 : part.inverseStage;
                Dictionary<string, object> stageItem = GetStageData(stageData, stage);
                stageItem["part_count"] = (int)stageItem["part_count"] + 1;
                stageItem["part_mass_tonnes"] = (double)stageItem["part_mass_tonnes"] + partMass;
                stageItem["propellant_mass_tonnes"] = (double)stageItem["propellant_mass_tonnes"] + partPropellant;

                if (part.Modules == null) continue;
                foreach (PartModule module in part.Modules)
                {
                    if (module == null || !IsEngineModule(module)) continue;
                    double vacuumBaseThrust = Math.Max(0d, MemberNumber(module, "maxThrust", 0d));
                    double seaIsp = Math.Max(1d, CurveValue(MemberValue(module, "atmosphereCurve"), 1f, 1d));
                    double vacuumIsp = Math.Max(seaIsp, CurveValue(MemberValue(module, "atmosphereCurve"), 0f, seaIsp));
                    // KSP's ModuleEngines maxThrust is the vacuum-rated
                    // value; sea-level thrust follows the Isp ratio at the
                    // local pressure. Keeping both values prevents the
                    // validator from counting vacuum thrust as launch-pad
                    // thrust and overstating TWR.
                    double vacuumThrust = vacuumBaseThrust;
                    double seaThrust = vacuumThrust * seaIsp / Math.Max(1d, vacuumIsp);
                    engineCount++;
                    totalSeaLevelThrust += seaThrust;
                    totalVacuumThrust += vacuumThrust;
                    totalSeaIspThrust += seaThrust * seaIsp;
                    totalVacuumIspThrust += vacuumThrust * vacuumIsp;
                    stageItem["engine_count"] = (int)stageItem["engine_count"] + 1;
                    stageItem["sea_level_thrust_kN"] = (double)stageItem["sea_level_thrust_kN"] + seaThrust;
                    stageItem["vacuum_thrust_kN"] = (double)stageItem["vacuum_thrust_kN"] + vacuumThrust;
                    stageItem["thrust_kN"] = (double)stageItem["thrust_kN"] + vacuumThrust;
                    stageItem["sea_isp_s"] = (double)stageItem["sea_isp_s"] + seaThrust * seaIsp;
                    stageItem["vacuum_isp_s"] = (double)stageItem["vacuum_isp_s"] + vacuumThrust * vacuumIsp;

                    object transforms = MemberValue(module, "thrustTransforms");
                    Transform thrustTransform = null;
                    IEnumerable transformList = transforms as IEnumerable;
                    if (transformList != null)
                    {
                        foreach (object rawTransform in transformList)
                        {
                            thrustTransform = rawTransform as Transform;
                            if (thrustTransform != null) break;
                        }
                    }
                    Vector3 thrustPosition = thrustTransform == null ? part.transform.position : thrustTransform.position;
                    centerThrustSum += thrustPosition * (float)vacuumThrust;
                    if (includeParts)
                    {
                        engineReports.Add(new Dictionary<string, object>
                        {
                            { "part_id", PartId(part) },
                            { "part", part.partInfo == null ? part.partName : part.partInfo.name },
                            { "stage", stage },
                            { "module", module.GetType().Name },
                            { "thrust_kN_vacuum", vacuumThrust },
                            { "thrust_kN_sea_level", seaThrust },
                            { "sea_level_isp_s", seaIsp },
                            { "vacuum_isp_s", vacuumIsp },
                            { "thrust_position", JsonUtil.Vector3Object(thrustPosition) }
                        });
                    }
                }
            }

            var warnings = new List<object>();
            var errors = new List<object>();
            Vector3 centerMass = totalMass <= 0d ? Vector3.zero : centerMassSum / (float)totalMass;
            Vector3 centerThrust = totalVacuumThrust <= 0d ? Vector3.zero : centerThrustSum / (float)totalVacuumThrust;
            double weightedSeaIsp = totalSeaLevelThrust <= 0d ? 0d : totalSeaIspThrust / totalSeaLevelThrust;
            double weightedVacuumIsp = totalVacuumThrust <= 0d ? 0d : totalVacuumIspThrust / totalVacuumThrust;
            int launchStage = -1;
            foreach (int candidateStage in stageData.Keys)
            {
                if ((int)stageData[candidateStage]["engine_count"] > 0) launchStage = Math.Max(launchStage, candidateStage);
            }
            double launchSeaLevelThrust = launchStage < 0 ? 0d : (double)stageData[launchStage]["sea_level_thrust_kN"];
            double launchVacuumThrust = launchStage < 0 ? 0d : (double)stageData[launchStage]["vacuum_thrust_kN"];
            double seaTwr = totalMass <= 0d ? 0d : launchSeaLevelThrust / (totalMass * Gravity);
            double vacuumTwr = totalMass <= 0d ? 0d : launchVacuumThrust / (totalMass * Gravity);
            double comAboveThrust = centerMass.y - centerThrust.y;
            double estimatedDv = 0d;

            if (engineCount == 0)
            {
                errors.Add("craft has no engine module");
            }
            else
            {
                if (seaTwr < 1.0d) errors.Add("estimated liftoff TWR is below 1.0; the craft cannot climb from the launch pad");
                else if (seaTwr < 1.20d) warnings.Add("estimated liftoff TWR is below 1.20; ascent will be slow and drag losses may be high");
                Part rootPart = null;
                foreach (Part candidate in EditorLogic.fetch.ship.parts)
                {
                    if (candidate != null && candidate.parent == null)
                    {
                        rootPart = candidate;
                        break;
                    }
                }
                Vector3 localCenterMass = rootPart == null ? centerMass : rootPart.transform.InverseTransformPoint(centerMass);
                Vector3 localCenterThrust = rootPart == null ? centerThrust : rootPart.transform.InverseTransformPoint(centerThrust);
                comAboveThrust = localCenterMass.y - localCenterThrust.y;
                if (comAboveThrust <= 0.05d)
                {
                    errors.Add("center of mass is not above the center of thrust in the VAB vertical axis; the rocket may pitch over");
                }
                else if (comAboveThrust < 0.20d)
                {
                    warnings.Add("center of mass is only slightly above the center of thrust; add fins or improve the mass/thrust layout");
                }

                List<int> stageNumbers = new List<int>(stageData.Keys);
                stageNumbers.Sort();
                foreach (int stage in stageNumbers)
                {
                    Dictionary<string, object> item = stageData[stage];
                    double stageThrust = (double)item["vacuum_thrust_kN"];
                    double stageSeaThrust = (double)item["sea_level_thrust_kN"];
                    double stagePropellant = (double)item["propellant_mass_tonnes"];
                    double remainingMass = 0d;
                    foreach (Part candidate in EditorLogic.fetch.ship.parts)
                    {
                        if (candidate == null) continue;
                        int candidateStage = candidate.inverseStage < 0 ? 0 : candidate.inverseStage;
                        if (candidate.inverseStage < 0 || candidateStage <= stage) remainingMass += SafePartMass(candidate);
                    }
                    double stageSeaTwr = remainingMass <= 0d ? 0d : stageSeaThrust / (remainingMass * Gravity);
                    double stageVacuumTwr = remainingMass <= 0d ? 0d : stageThrust / (remainingMass * Gravity);
                    double stageVacuumIsp = (double)item["vacuum_isp_s"] <= 0d ? 0d : (double)item["vacuum_isp_s"] / Math.Max(0.001d, stageThrust);
                    double finalMass = Math.Max(0.001d, remainingMass - stagePropellant);
                    double stageDv = stageVacuumIsp <= 0d || remainingMass <= finalMass ? 0d : stageVacuumIsp * Gravity * Math.Log(remainingMass / finalMass);
                    item["remaining_mass_tonnes"] = remainingMass;
                    item["twr_sea_level"] = stageSeaTwr;
                    item["twr_vacuum"] = stageVacuumTwr;
                    item["vacuum_isp_s"] = stageVacuumIsp;
                    item["delta_v_mps_estimate"] = stageDv;
                    estimatedDv += stageDv;
                    if (stageThrust > 0d && stageSeaTwr < 1.0d)
                    {
                        errors.Add("stage " + stage + " estimated TWR is below 1.0");
                    }
                }
            }

            var stageSummary = new List<object>();
            foreach (int stage in new List<int>(stageData.Keys))
            {
                stageSummary.Add(stageData[stage]);
            }
            stageSummary.Sort(delegate(object left, object right)
            {
                return ((int)((Dictionary<string, object>)left)["stage"]).CompareTo((int)((Dictionary<string, object>)right)["stage"]);
            });

            return new Dictionary<string, object>
            {
                { "estimate_method", "KSP part mass/resources and engine atmosphere curves; excludes drag, steering losses, boiloff, and crossfeed changes" },
                { "launch_safe_estimate", errors.Count == 0 },
                { "errors", errors },
                { "warnings", warnings },
                { "mass_tonnes", totalMass },
                { "propellant_mass_tonnes", totalPropellantMass },
                { "engine_count", engineCount },
                { "launch_stage", launchStage },
                { "thrust_kN_sea_level", launchSeaLevelThrust },
                { "thrust_kN_vacuum", launchVacuumThrust },
                { "twr_sea_level", seaTwr },
                { "twr_vacuum", vacuumTwr },
                { "sea_level_isp_s", weightedSeaIsp },
                { "vacuum_isp_s", weightedVacuumIsp },
                { "delta_v_mps_estimate", estimatedDv },
                { "center_of_mass", JsonUtil.Vector3Object(centerMass) },
                { "center_of_thrust", JsonUtil.Vector3Object(centerThrust) },
                { "com_above_thrust_m", totalVacuumThrust <= 0d ? 0d : comAboveThrust },
                { "stage_summary", stageSummary },
                { "engines", engineReports }
            };
        }

        private static Dictionary<string, object> GetStageData(Dictionary<int, Dictionary<string, object>> stages, int stage)
        {
            Dictionary<string, object> item;
            if (stages.TryGetValue(stage, out item)) return item;
            item = new Dictionary<string, object>
            {
                { "stage", stage },
                { "part_count", 0 },
                { "engine_count", 0 },
                { "part_mass_tonnes", 0d },
                { "propellant_mass_tonnes", 0d },
                { "sea_level_thrust_kN", 0d },
                { "vacuum_thrust_kN", 0d },
                { "thrust_kN", 0d },
                { "sea_isp_s", 0d },
                { "vacuum_isp_s", 0d }
            };
            stages[stage] = item;
            return item;
        }

        private static bool IsEngineModule(PartModule module)
        {
            return module != null && module.GetType().Name.IndexOf("ModuleEngines", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static double SafePartMass(Part part)
        {
            if (part == null) return 0d;
            double mass = Math.Max(0d, part.mass);
            try { mass += Math.Max(0d, part.GetResourceMass()); } catch (Exception) { }
            return mass;
        }

        private static double SafePropellantMass(Part part)
        {
            if (part == null || part.Resources == null) return 0d;
            double mass = 0d;
            foreach (PartResource resource in part.Resources)
            {
                if (resource == null) continue;
                string name = resource.resourceName ?? "";
                if (name.Equals("ElectricCharge", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Ablator", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Ore", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    PartResourceDefinition definition = PartResourceLibrary.Instance.GetDefinition(name);
                    if (definition != null) mass += Math.Max(0d, resource.amount * definition.density);
                }
                catch (Exception) { }
            }
            return mass;
        }

        private static object MemberValue(object target, string name)
        {
            if (target == null) return null;
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            try
            {
                FieldInfo field = target.GetType().GetField(name, flags);
                if (field != null) return field.GetValue(target);
                PropertyInfo property = target.GetType().GetProperty(name, flags);
                if (property != null) return property.GetValue(target, null);
            }
            catch (Exception) { }
            return null;
        }

        private static double MemberNumber(object target, string name, double fallback)
        {
            object value = MemberValue(target, name);
            if (value == null) return fallback;
            try { return Convert.ToDouble(value); } catch (Exception) { return fallback; }
        }

        private static double CurveValue(object curve, float atmosphere, double fallback)
        {
            if (curve == null) return fallback;
            try
            {
                MethodInfo method = curve.GetType().GetMethod("Evaluate", new[] { typeof(float) });
                if (method != null) return Convert.ToDouble(method.Invoke(curve, new object[] { atmosphere }));
            }
            catch (Exception) { }
            return fallback;
        }

        public Dictionary<string, object> Save(Dictionary<string, object> args)
        {
            EnsureEditor();
            string requestedName = JsonUtil.String(args, "name", _craftName);
            if (!string.IsNullOrEmpty(requestedName)) _craftName = requestedName;
            string path = JsonUtil.String(args, "path", null);
            if (string.IsNullOrEmpty(path))
            {
                string folder = string.IsNullOrEmpty(HighLogic.SaveFolder) ? "default" : HighLogic.SaveFolder;
                string directory = Path.Combine(KSPUtil.ApplicationRootPath, "saves", folder, "Ships", _editorMode);
                Directory.CreateDirectory(directory);
                path = Path.Combine(directory, SafeFileName(_craftName) + ".craft");
            }
            if (!path.EndsWith(".craft", StringComparison.OrdinalIgnoreCase)) path += ".craft";
            if (File.Exists(path) && !JsonUtil.Boolean(args, "overwrite", false))
            {
                throw new KspMcpException("file_exists", "craft file already exists; pass overwrite=true", path);
            }

            EnsureEditorRootPlacement(true);
            RestoreRequestedStages();
            SetShipMetadata();
            ConfigNode root = EditorLogic.fetch.ship.SaveShip();
            if (root == null) throw new KspMcpException("save_failed", "KSP returned an empty craft node", null);
            root.Save(path);
            return new Dictionary<string, object> { { "saved", true }, { "path", path }, { "name", _craftName }, { "part_count", _partsById.Count } };
        }

        public Dictionary<string, object> Load(Dictionary<string, object> args)
        {
            EnsureEditor();
            _buildJob = null;
            string path = JsonUtil.String(args, "path", null);
            string mode = NormaliseMode(JsonUtil.String(args, "editor_mode", _editorMode));
            if (string.IsNullOrEmpty(path))
            {
                string name = JsonUtil.RequiredString(args, "name");
                string folder = string.IsNullOrEmpty(HighLogic.SaveFolder) ? "default" : HighLogic.SaveFolder;
                path = Path.Combine(KSPUtil.ApplicationRootPath, "saves", folder, "Ships", mode, SafeFileName(name) + ".craft");
            }
            if (!File.Exists(path)) throw new KspMcpException("file_not_found", "craft file not found: " + path, null);

            _editorMode = mode;
            _partsById.Clear();
            _idsByPart.Clear();
            _requestedStages.Clear();
            CaptureRequestedStages(path);
            EditorLogic.LoadShipFromFile(path);
            _craftName = Path.GetFileNameWithoutExtension(path);
            _loadPending = true;
            _loadFrames = 0;
            _loadLastPartCount = -1;
            _loadStableFrames = 0;
            return new Dictionary<string, object>
            {
                { "loaded", true },
                { "path", path },
                { "expected_part_count", _expectedLoadPartCount },
                { "note", "KSP has been asked to load the craft; the bridge will wait for the editor part tree to stabilize, restore stages, and place the VAB root above the floor" }
            };
        }

        public Dictionary<string, object> Launch(Dictionary<string, object> args)
        {
            EnsureEditor();
            if (!JsonUtil.Boolean(args, "confirm", false)) throw new KspMcpException("confirmation_required", "launch requires confirm=true", null);
            Dictionary<string, object> validation = Validate();
            if (!(validation["valid"] is bool) || !(bool)validation["valid"])
            {
                throw new KspMcpException("craft_invalid", "refusing to launch an invalid craft", validation);
            }
            // Validation checks the game-rule invariants. Analyze() adds the
            // launch-facing physics gate so a commandable craft with an
            // engine that cannot actually lift off is not sent into flight.
            Dictionary<string, object> analysis = Analyze(new Dictionary<string, object>());
            if (!(analysis["launch_safe_estimate"] is bool) || !(bool)analysis["launch_safe_estimate"])
            {
                throw new KspMcpException("launch_unsafe", "refusing to launch a craft that fails the estimated liftoff/TWR/geometry preflight", new Dictionary<string, object>
                {
                    { "validation", validation },
                    { "analysis", analysis }
                });
            }
            if (!InvokeLaunchButton()) throw new KspMcpException("launch_failed", "could not invoke the KSP editor launch button", null);
            return new Dictionary<string, object>
            {
                { "launch_requested", true },
                { "scene", KspMcpBridge.SceneName() },
                { "preflight", new Dictionary<string, object> { { "validation", validation }, { "analysis", analysis } } }
            };
        }

        public Dictionary<string, object> Snapshot()
        {
            EnsureEditor();
            SyncPartMap();
            var parts = new List<object>();
            foreach (Part part in EditorLogic.fetch.ship.parts)
            {
                if (part == null) continue;
                parts.Add(SnapshotPart(part));
            }

            return new Dictionary<string, object>
            {
                { "name", _craftName },
                { "description", _craftDescription },
                { "editor_mode", _editorMode },
                { "part_count", parts.Count },
                { "connected", SafeAreAllPartsConnected() },
                { "parts", parts }
            };
        }

        private Dictionary<string, object> SnapshotPart(Part part)
        {
            string id = PartId(part);
            var snapshot = new Dictionary<string, object>
            {
                { "id", id },
                { "part", part.partInfo == null ? part.partName : part.partInfo.name },
                { "title", part.partInfo == null ? part.partName : part.partInfo.title },
                { "position", JsonUtil.Vector3Object(part.transform.position) },
                { "rotation", JsonUtil.QuaternionObject(part.transform.rotation) },
                { "stage", part.inverseStage },
                { "mass_tonnes", part.mass },
                { "parent_id", part.parent == null ? null : PartId(part.parent) },
                { "modules", Modules(part) },
                { "actions", Actions(part) },
                { "resources", Resources(part) },
                { "custom_data", ReadPersistedCustomData(part) },
                { "attach_nodes", AttachNodes(part) }
            };

            if (part.parent != null)
            {
                AttachNode parentNode = part.parent.FindAttachNodeByPart(part);
                AttachNode childNode = null;
                if (part.attachNodes != null)
                {
                    foreach (AttachNode candidate in part.attachNodes)
                    {
                        if (candidate != null && candidate.attachedPart == part.parent) { childNode = candidate; break; }
                    }
                }
                snapshot["parent_attach_node"] = parentNode == null ? null : parentNode.id;
                snapshot["attach_node"] = childNode == null ? null : childNode.id;
            }
            return snapshot;
        }

        private static List<object> Modules(Part part)
        {
            var result = new List<object>();
            if (part.Modules == null) return result;
            foreach (PartModule module in part.Modules)
            {
                if (module == null) continue;
                result.Add(new Dictionary<string, object>
                {
                    { "name", ModuleName(module) },
                    { "gui_name", module.GUIName }
                });
            }
            return result;
        }

        private static List<object> Resources(Part part)
        {
            var result = new List<object>();
            if (part.Resources == null) return result;
            foreach (PartResource resource in part.Resources)
            {
                if (resource == null) continue;
                result.Add(new Dictionary<string, object>
                {
                    { "name", resource.resourceName },
                    { "amount", resource.amount },
                    { "max_amount", resource.maxAmount },
                    { "flow_state", resource.flowState }
                });
            }
            return result;
        }

        private static List<object> Actions(Part part)
        {
            var result = new List<object>();
            if (part.Actions == null) return result;
            foreach (BaseAction action in part.Actions)
            {
                if (action == null) continue;
                result.Add(new Dictionary<string, object>
                {
                    { "name", action.name },
                    { "gui_name", action.guiName },
                    { "group", action.actionGroup.ToString() },
                    { "active", action.active }
                });
            }
            return result;
        }

        private static List<object> AttachNodes(Part part)
        {
            var result = new List<object>();
            if (part.attachNodes == null) return result;
            foreach (AttachNode node in part.attachNodes)
            {
                if (node == null) continue;
                result.Add(new Dictionary<string, object>
                {
                    { "id", node.id },
                    { "type", node.nodeType.ToString() },
                    { "size", node.size },
                    { "position", JsonUtil.Vector3Object(node.position) },
                    { "orientation", JsonUtil.Vector3Object(node.orientation) },
                    { "occupied", node.attachedPart != null },
                    { "attached_part_id", node.attachedPart == null ? null : node.attachedPart.flightID.ToString() }
                });
            }
            return result;
        }

        public Dictionary<string, object> ListAvailableParts(Dictionary<string, object> args)
        {
            List<AvailablePart> loaded = LoadedParts();
            string query = JsonUtil.String(args, "query", "");
            bool includeModules = JsonUtil.Boolean(args, "include_modules", true);
            int limit = Math.Max(1, Math.Min(1000, JsonUtil.Integer(args, "limit", 200)));
            query = query.ToLowerInvariant();
            var output = new List<object>();
            foreach (AvailablePart part in loaded)
            {
                if (part == null) continue;
                string haystack = ((part.name ?? "") + " " + (part.title ?? "") + " " + (part.manufacturer ?? "")).ToLowerInvariant();
                if (query.Length > 0 && haystack.IndexOf(query, StringComparison.Ordinal) < 0) continue;
                var item = new Dictionary<string, object>
                {
                    { "part", part.name },
                    { "title", part.title },
                    { "manufacturer", part.manufacturer },
                    { "description", part.description },
                    { "cost", part.cost },
                    { "mass_tonnes", part.partPrefab == null ? 0f : part.partPrefab.mass },
                    { "tech_required", part.TechRequired },
                    { "part_size", part.partSize },
                    { "attach_nodes", part.partPrefab == null ? new List<object>() : AttachNodes(part.partPrefab) }
                };
                if (includeModules) item["modules"] = part.partPrefab == null ? new List<object>() : Modules(part.partPrefab);
                output.Add(item);
                if (output.Count >= limit) break;
            }
            return new Dictionary<string, object> { { "count", output.Count }, { "parts", output } };
        }

        private void EnsureEditor()
        {
            if (!IsEditorAvailable) throw new KspMcpException("not_in_editor", "KSP must be in the VAB or SPH editor for this command", KspMcpBridge.SceneName());
        }

        private void ClearInternal()
        {
            _loadPending = false;
            _loadFrames = 0;
            _loadLastPartCount = -1;
            _loadStableFrames = 0;
            _expectedLoadPartCount = 0;
            if (IsEditorAvailable)
            {
                var parts = new List<Part>(EditorLogic.fetch.ship.parts);
                foreach (Part part in parts)
                {
                    if (part == null) continue;
                    try
                    {
                        EditorLogic.DeletePart(part);
                    }
                    catch (Exception)
                    {
                        try { EditorLogic.fetch.ship.Remove(part); } catch (Exception) { }
                        UnityEngine.Object.Destroy(part.gameObject);
                    }
                }
                try { EditorLogic.fetch.ship.Clear(); } catch (Exception) { }
            }
            _partsById.Clear();
            _idsByPart.Clear();
            _requestedStages.Clear();
        }

        private void SetShipMetadata()
        {
            if (!IsEditorAvailable) return;
            EditorLogic.fetch.ship.shipName = _craftName;
            EditorLogic.fetch.ship.shipDescription = _craftDescription;
        }

        private void EnsureEditorRootPlacement()
        {
            EnsureEditorRootPlacement(false);
        }

        private void EnsureEditorRootPlacement(bool inspectBounds)
        {
            if (!IsEditorAvailable || !string.Equals(_editorMode, "VAB", StringComparison.OrdinalIgnoreCase)) return;

            Part root = null;
            foreach (Part candidate in EditorLogic.fetch.ship.parts)
            {
                if (candidate == null || candidate.parent != null) continue;
                if (root != null) return;
                root = candidate;
            }
            if (root == null) return;

            Vector3 position = root.transform.position;
            if (position.y < EditorRootHeight)
            {
                position.y = EditorRootHeight;
                root.transform.position = position;
            }

            // The fixed root-height guard prevents the common underground
            // placement case while a build is in progress. At a completed
            // build/load boundary, inspect actual renderer/collider bounds as
            // well: tall or rotated assemblies can extend below the editor
            // floor even when the root transform itself is above it.
            if (!inspectBounds) return;
            float lowestY = float.PositiveInfinity;
            foreach (Part part in EditorLogic.fetch.ship.parts)
            {
                if (part == null) continue;
                lowestY = Math.Min(lowestY, part.transform.position.y - 0.5f);
                Collider[] colliders = part.GetComponentsInChildren<Collider>(true);
                foreach (Collider collider in colliders)
                {
                    if (collider != null) lowestY = Math.Min(lowestY, collider.bounds.min.y);
                }
                Renderer[] renderers = part.GetComponentsInChildren<Renderer>(true);
                foreach (Renderer renderer in renderers)
                {
                    if (renderer != null) lowestY = Math.Min(lowestY, renderer.bounds.min.y);
                }
            }
            const float floorClearance = 0.5f;
            if (!float.IsPositiveInfinity(lowestY) && lowestY < floorClearance)
            {
                root.transform.position += Vector3.up * (floorClearance - lowestY);
            }
        }

        private void CaptureRequestedStages(string path)
        {
            _expectedLoadPartCount = 0;
            try
            {
                ConfigNode root = ConfigNode.Load(path);
                if (root == null) return;
                ConfigNode[] nodes = root.GetNodes("PART");
                if (nodes == null) return;
                _expectedLoadPartCount = nodes.Length;
                foreach (ConfigNode node in nodes)
                {
                    if (node == null) continue;
                    string id = ExtractPersistedId(node.GetValue("cData"));
                    int stage;
                    if (!string.IsNullOrEmpty(id) && int.TryParse(node.GetValue("istg"), out stage) && stage >= 0)
                    {
                        _requestedStages[id] = stage;
                    }
                }
            }
            catch (Exception exception)
            {
                KspMcpBridge.Log("could not read saved craft stages before load: " + exception.Message);
                _expectedLoadPartCount = 0;
            }
        }

        private void SyncPartMap()
        {
            if (!IsEditorAvailable) return;
            var live = new HashSet<Part>();
            foreach (Part part in EditorLogic.fetch.ship.parts)
            {
                if (part == null) continue;
                live.Add(part);
                string id;
                if (!_idsByPart.TryGetValue(part, out id) || string.IsNullOrEmpty(id))
                {
                    id = ReadPersistedId(part);
                    if (string.IsNullOrEmpty(id)) id = "part-" + part.flightID;
                    while (_partsById.ContainsKey(id) && _partsById[id] != part) id += "-1";
                    _idsByPart[part] = id;
                }
                _partsById[id] = part;
            }

            var stale = new List<Part>();
            foreach (KeyValuePair<Part, string> item in _idsByPart) if (!live.Contains(item.Key)) stale.Add(item.Key);
            foreach (Part part in stale)
            {
                string id = _idsByPart[part];
                _idsByPart.Remove(part);
                if (_partsById.ContainsKey(id) && _partsById[id] == part) _partsById.Remove(id);
            }
        }

        private string ReadPersistedId(Part part)
        {
            return ExtractPersistedId(part == null ? null : part.customPartData);
        }

        private static string ExtractPersistedId(string data)
        {
            if (data == null || data.IndexOf(IdPrefix, StringComparison.Ordinal) < 0) return null;
            int start = data.IndexOf(IdPrefix, StringComparison.Ordinal) + IdPrefix.Length;
            int end = data.IndexOf(';', start);
            if (end < 0) end = data.Length;
            return data.Substring(start, end - start);
        }

        private static string BuildCustomPartData(string id, object customData)
        {
            string result = IdPrefix + id;
            if (customData == null) return result;
            string json = McpJson.Serialize(customData);
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
            return result + ";" + CustomDataPrefix + encoded;
        }

        private static object ReadPersistedCustomData(Part part)
        {
            try
            {
                string data = part == null ? null : part.customPartData;
                if (string.IsNullOrEmpty(data)) return null;
                int start = data.IndexOf(CustomDataPrefix, StringComparison.Ordinal);
                if (start < 0) return null;
                start += CustomDataPrefix.Length;
                int end = data.IndexOf(';', start);
                if (end < 0) end = data.Length;
                string encoded = data.Substring(start, end - start);
                if (encoded.Length == 0) return null;
                string json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                return McpJson.Deserialize(json);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private Part FindPart(string id)
        {
            SyncPartMap();
            Part part;
            return id != null && _partsById.TryGetValue(id, out part) ? part : null;
        }

        public Part FindPartForControl(string id)
        {
            Part part = FindPart(id);
            if (part != null) return part;
            Vessel vessel = null;
            try { vessel = FlightGlobals.ActiveVessel; } catch (Exception) { }
            if (vessel == null || vessel.parts == null) return null;
            foreach (Part candidate in vessel.parts)
            {
                if (candidate != null && string.Equals(ReadPersistedId(candidate), id, StringComparison.Ordinal)) return candidate;
            }
            return null;
        }

        private string PartId(Part part)
        {
            SyncPartMap();
            string id;
            if (_idsByPart.TryGetValue(part, out id)) return id;
            id = "part-" + part.flightID;
            _idsByPart[part] = id;
            _partsById[id] = part;
            return id;
        }

        private static string ModuleName(PartModule module)
        {
            if (module == null) return "";
            try { return string.IsNullOrEmpty(module.ClassName) ? module.moduleName : module.ClassName; }
            catch (Exception) { return module.GetType().Name; }
        }

        private bool SafeAreAllPartsConnected()
        {
            try { return IsEditorAvailable && EditorLogic.fetch.ship.AreAllPartsConnected(); }
            catch (Exception) { return false; }
        }

        private static string NormaliseMode(string mode)
        {
            return string.Equals(mode, "SPH", StringComparison.OrdinalIgnoreCase) ? "SPH" : "VAB";
        }

        private static string SafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "MCP Craft";
            string result = name;
            foreach (char invalid in Path.GetInvalidFileNameChars()) result = result.Replace(invalid.ToString(), "_");
            return result.Trim();
        }

        private uint NextFlightId()
        {
            try
            {
                MethodInfo method = typeof(FlightGlobals).GetMethod("GetUniquepersistentId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (method != null) return Convert.ToUInt32(method.Invoke(null, null));
            }
            catch (Exception) { }
            return _fallbackFlightId++;
        }

        private static AvailablePart ResolveAvailablePart(string name)
        {
            try
            {
                MethodInfo method = typeof(PartLoader).GetMethod("getPartInfoByName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (method != null)
                {
                    AvailablePart result = method.Invoke(null, new object[] { name }) as AvailablePart;
                    if (result != null) return result;
                }
            }
            catch (Exception) { }

            foreach (AvailablePart part in LoadedParts())
            {
                if (part != null && string.Equals(part.name, name, StringComparison.OrdinalIgnoreCase)) return part;
            }
            return null;
        }

        private static List<AvailablePart> LoadedParts()
        {
            var result = new List<AvailablePart>();
            object value = null;
            try
            {
                MethodInfo method = typeof(PartLoader).GetMethod("getLoadedPartsList", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (method != null) value = method.Invoke(null, null);
            }
            catch (Exception) { }
            if (value == null)
            {
                try
                {
                    PropertyInfo property = typeof(PartLoader).GetProperty("LoadedPartsList", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (property != null) value = property.GetValue(null, null);
                }
                catch (Exception) { }
            }
            if (value == null)
            {
                try
                {
                    FieldInfo field = typeof(PartLoader).GetField("LoadedPartsList", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (field != null) value = field.GetValue(null);
                }
                catch (Exception) { }
            }
            IEnumerable enumerable = value as IEnumerable;
            if (enumerable != null)
            {
                foreach (object item in enumerable)
                {
                    AvailablePart part = item as AvailablePart;
                    if (part != null && !result.Contains(part)) result.Add(part);
                }
            }
            return result;
        }

        private static bool InvokeLaunchButton()
        {
            object button = EditorLogic.fetch == null ? null : (object)EditorLogic.fetch.launchBtn;
            if (button == null) return false;
            if (InvokeZeroArgument(button, "OnClick") || InvokeZeroArgument(button, "Click") || InvokeZeroArgument(button, "Invoke")) return true;

            Type type = button.GetType();
            foreach (string memberName in new[] { "onClick", "OnClick" })
            {
                FieldInfo field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                object callback = field == null ? null : field.GetValue(button);
                if (callback != null && (InvokeZeroArgument(callback, "Invoke") || InvokeZeroArgument(callback, "OnClick"))) return true;
                PropertyInfo property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                callback = property == null ? null : property.GetValue(button, null);
                if (callback != null && (InvokeZeroArgument(callback, "Invoke") || InvokeZeroArgument(callback, "OnClick"))) return true;
            }
            return false;
        }

        private static bool InvokeZeroArgument(object target, string methodName)
        {
            MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (method == null) return false;
            method.Invoke(target, null);
            return true;
        }

        private static void FireEditorModified()
        {
            try
            {
                if (EditorLogic.fetch != null && EditorLogic.fetch.ship != null) GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
            }
            catch (Exception) { }
        }
    }
}

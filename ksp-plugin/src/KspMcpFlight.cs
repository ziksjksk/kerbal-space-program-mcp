using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace KspMcp
{
    internal sealed class KspMcpFlight
    {
        private readonly KspMcpCraft _craft;
        private readonly ControlLease _lease = new ControlLease();
        private Vessel _hookedVessel;
        private double _leaseUntil;
        private bool _sasEnabled;
        private bool _rcsEnabled;
        private GuidancePlan _guidance;
        private double _lastGuidanceStageAt;
        private bool _haveTelemetryIdentity;
        private string _lastTelemetryVesselId;
        private string _lastTelemetrySituation;
        private string _lastTelemetryBody;
        private int _lastTelemetryStage = int.MinValue;
        private bool _lastTelemetryCommandable;
        private string _lastTelemetryGuidancePhase;
        private float _lastCompactSummaryAt = -1f;
        private string _compactSummaryVesselId;
        private Dictionary<string, object> _compactEngineSummary;
        private List<object> _compactResources;

        private sealed class GuidancePlan
        {
            public string Profile;
            public double TargetApoapsis;
            public double TargetPeriapsis;
            public double TargetAltitude;
            public double StartedAt;
            public double EndsAt;
            public bool AutoStage;
            public string Phase;
            public string LastError;
            public double LastThrottle;
            public double LastPitch;
            public double LastYaw;
            public double LastTargetPitchDegrees;
            public int BurnNodeIndex;
            public double BurnUt;
            public double BurnDuration;
            public double BurnThrottle;
            public double BurnDeltaV;
            public bool BurnCompletionRecorded;
        }

        private sealed class ControlLease
        {
            public bool HasThrottle;
            public float Throttle;
            public bool HasPitch;
            public float Pitch;
            public bool HasYaw;
            public float Yaw;
            public bool HasRoll;
            public float Roll;
            public bool HasX;
            public float X;
            public bool HasY;
            public float Y;
            public bool HasZ;
            public float Z;
            public bool HasWheelSteer;
            public float WheelSteer;
            public bool HasWheelThrottle;
            public float WheelThrottle;
            public bool HasGear;
            public bool Gear;
            public bool HasBrakes;
            public bool Brakes;
            public bool HasLights;
            public bool Lights;

            public void Clear()
            {
                HasThrottle = HasPitch = HasYaw = HasRoll = HasX = HasY = HasZ = false;
                HasWheelSteer = HasWheelThrottle = HasGear = HasBrakes = HasLights = false;
            }
        }

        public KspMcpFlight(KspMcpCraft craft)
        {
            _craft = craft;
        }

        public void Start()
        {
            // The active vessel changes on launch, staging separation, docking,
            // and vessel switching. Tick() keeps the callback attached to the
            // vessel that actually receives player controls.
        }

        public void Tick()
        {
            Vessel active = null;
            try { active = FlightGlobals.ActiveVessel; } catch (Exception) { }
            if (active != _hookedVessel)
            {
                if (_hookedVessel != null) _hookedVessel.OnFlyByWire -= OnFlyByWire;
                _hookedVessel = active;
                if (_hookedVessel != null) _hookedVessel.OnFlyByWire += OnFlyByWire;
            }
            if (_leaseUntil > 0d && Planetarium.GetUniversalTime() > _leaseUntil)
            {
                _lease.Clear();
                _leaseUntil = 0d;
            }
            if (_guidance != null)
            {
                if (Planetarium.GetUniversalTime() > _guidance.EndsAt)
                {
                    _guidance.LastError = "guidance time limit reached";
                    _guidance = null;
                }
                else if (_guidance.AutoStage && Planetarium.GetUniversalTime() - _lastGuidanceStageAt > 0.25d)
                {
                    TryAutomaticStage(active);
                }
            }
            RecordTelemetryTransitions(active);
        }

        public void Stop()
        {
            if (_hookedVessel != null) _hookedVessel.OnFlyByWire -= OnFlyByWire;
            _hookedVessel = null;
            _lease.Clear();
            _leaseUntil = 0d;
            _guidance = null;
            _haveTelemetryIdentity = false;
            _lastTelemetryVesselId = null;
            _lastTelemetrySituation = null;
            _lastTelemetryBody = null;
            _lastTelemetryStage = int.MinValue;
            _lastTelemetryGuidancePhase = null;
            _lastCompactSummaryAt = -1f;
            _compactSummaryVesselId = null;
            _compactEngineSummary = null;
            _compactResources = null;
        }

        private void RecordTelemetryTransitions(Vessel vessel)
        {
            KspMcpBridge bridge = KspMcpBridge.Instance;
            if (bridge == null) return;
            if (vessel == null)
            {
                if (_haveTelemetryIdentity)
                {
                    bridge.RecordEvent("flight.vessel.unavailable", new Dictionary<string, object>
                    {
                        { "vessel_id", _lastTelemetryVesselId }
                    });
                }
                _haveTelemetryIdentity = false;
                _lastTelemetryVesselId = null;
                _lastTelemetrySituation = null;
                _lastTelemetryBody = null;
                _lastTelemetryStage = int.MinValue;
                _lastTelemetryGuidancePhase = null;
                return;
            }

            string vesselId = vessel.id.ToString();
            string situation = vessel.situation.ToString();
            string body = vessel.mainBody == null ? null : vessel.mainBody.bodyName;
            int stage = vessel.currentStage;
            bool commandable = vessel.isCommandable;
            if (!_haveTelemetryIdentity || !string.Equals(_lastTelemetryVesselId, vesselId, StringComparison.Ordinal))
            {
                bridge.RecordEvent("flight.vessel.changed", new Dictionary<string, object>
                {
                    { "vessel_id", vesselId },
                    { "vessel_name", vessel.vesselName },
                    { "body", body }
                });
                _haveTelemetryIdentity = true;
                _lastTelemetryVesselId = vesselId;
                _lastTelemetrySituation = null;
                _lastTelemetryBody = null;
                _lastTelemetryStage = int.MinValue;
            }
            if (!string.Equals(_lastTelemetrySituation, situation, StringComparison.Ordinal))
            {
                bridge.RecordEvent("flight.situation.changed", new Dictionary<string, object>
                {
                    { "vessel_id", vesselId },
                    { "from", _lastTelemetrySituation },
                    { "to", situation }
                });
                _lastTelemetrySituation = situation;
            }
            if (!string.Equals(_lastTelemetryBody, body, StringComparison.Ordinal))
            {
                bridge.RecordEvent("flight.body.changed", new Dictionary<string, object>
                {
                    { "vessel_id", vesselId },
                    { "from", _lastTelemetryBody },
                    { "to", body }
                });
                _lastTelemetryBody = body;
            }
            if (_lastTelemetryStage != stage)
            {
                bridge.RecordEvent("flight.stage.changed", new Dictionary<string, object>
                {
                    { "vessel_id", vesselId },
                    { "stage", stage }
                });
                _lastTelemetryStage = stage;
            }
            if (_lastTelemetryCommandable != commandable)
            {
                bridge.RecordEvent("flight.commandability.changed", new Dictionary<string, object>
                {
                    { "vessel_id", vesselId },
                    { "commandable", commandable }
                });
                _lastTelemetryCommandable = commandable;
            }
            string guidancePhase = _guidance == null ? "inactive" : _guidance.Phase;
            if (!string.Equals(_lastTelemetryGuidancePhase, guidancePhase, StringComparison.Ordinal))
            {
                bridge.RecordEvent("flight.guidance.phase", new Dictionary<string, object>
                {
                    { "vessel_id", vesselId },
                    { "phase", guidancePhase },
                    { "profile", _guidance == null ? null : _guidance.Profile }
                });
                _lastTelemetryGuidancePhase = guidancePhase;
            }
        }

        private void OnFlyByWire(FlightCtrlState state)
        {
            if (_guidance != null)
            {
                ApplyGuidance(state);
                return;
            }
            if (_leaseUntil <= 0d || Planetarium.GetUniversalTime() > _leaseUntil) return;

            if (_lease.HasThrottle) state.mainThrottle = _lease.Throttle;
            if (_lease.HasPitch) state.pitch = _lease.Pitch;
            if (_lease.HasYaw) state.yaw = _lease.Yaw;
            if (_lease.HasRoll) state.roll = _lease.Roll;
            if (_lease.HasX) state.X = _lease.X;
            if (_lease.HasY) state.Y = _lease.Y;
            if (_lease.HasZ) state.Z = _lease.Z;
            if (_lease.HasGear)
            {
                state.gearDown = _lease.Gear;
                state.gearUp = !_lease.Gear;
            }
            if (_lease.HasLights) state.headlight = _lease.Lights;
            if (_sasEnabled) state.killRot = true;

            // Wheel and brake fields differ slightly between old KSP 1.x
            // revisions, so they are set by name when that revision exposes
            // them rather than making the whole plugin version-specific.
            if (_lease.HasWheelSteer) SetMember(state, "wheelSteer", _lease.WheelSteer);
            if (_lease.HasWheelThrottle) SetMember(state, "wheelThrottle", _lease.WheelThrottle);
            if (_lease.HasBrakes) SetMember(state, "brakes", _lease.Brakes);
        }

        public Dictionary<string, object> SetControls(Dictionary<string, object> args)
        {
            EnsureFlight();
            if (_guidance != null) throw new KspMcpException("guidance_active", "manual controls are locked while a guidance plan is active; stop guidance first", GuidanceStatus());
            if (JsonUtil.Has(args, "throttle")) { _lease.HasThrottle = true; _lease.Throttle = Clamp(JsonUtil.ToFloat(JsonUtil.Get(args, "throttle")), 0f, 1f); }
            if (JsonUtil.Has(args, "pitch")) { _lease.HasPitch = true; _lease.Pitch = Clamp(JsonUtil.ToFloat(JsonUtil.Get(args, "pitch")), -1f, 1f); }
            if (JsonUtil.Has(args, "yaw")) { _lease.HasYaw = true; _lease.Yaw = Clamp(JsonUtil.ToFloat(JsonUtil.Get(args, "yaw")), -1f, 1f); }
            if (JsonUtil.Has(args, "roll")) { _lease.HasRoll = true; _lease.Roll = Clamp(JsonUtil.ToFloat(JsonUtil.Get(args, "roll")), -1f, 1f); }
            if (JsonUtil.Has(args, "translate_x")) { _lease.HasX = true; _lease.X = Clamp(JsonUtil.ToFloat(JsonUtil.Get(args, "translate_x")), -1f, 1f); }
            if (JsonUtil.Has(args, "translate_y")) { _lease.HasY = true; _lease.Y = Clamp(JsonUtil.ToFloat(JsonUtil.Get(args, "translate_y")), -1f, 1f); }
            if (JsonUtil.Has(args, "translate_z")) { _lease.HasZ = true; _lease.Z = Clamp(JsonUtil.ToFloat(JsonUtil.Get(args, "translate_z")), -1f, 1f); }
            if (JsonUtil.Has(args, "wheel_steer")) { _lease.HasWheelSteer = true; _lease.WheelSteer = Clamp(JsonUtil.ToFloat(JsonUtil.Get(args, "wheel_steer")), -1f, 1f); }
            if (JsonUtil.Has(args, "wheel_throttle")) { _lease.HasWheelThrottle = true; _lease.WheelThrottle = Clamp(JsonUtil.ToFloat(JsonUtil.Get(args, "wheel_throttle")), -1f, 1f); }

            if (JsonUtil.Has(args, "gear"))
            {
                _lease.HasGear = true;
                _lease.Gear = JsonUtil.Boolean(args, "gear", false);
                FireGroup("Gear", _lease.Gear);
            }
            if (JsonUtil.Has(args, "brakes"))
            {
                _lease.HasBrakes = true;
                _lease.Brakes = JsonUtil.Boolean(args, "brakes", false);
                FireGroup("Brakes", _lease.Brakes);
            }
            if (JsonUtil.Has(args, "lights"))
            {
                _lease.HasLights = true;
                _lease.Lights = JsonUtil.Boolean(args, "lights", false);
                FireGroup("Light", _lease.Lights);
            }

            double seconds = Math.Max(0.1, Math.Min(30.0, JsonUtil.Number(args, "lease_seconds", 1.5)));
            _leaseUntil = Planetarium.GetUniversalTime() + seconds;
            return new Dictionary<string, object> { { "control_lease_seconds", seconds }, { "controls", ControlSnapshot() } };
        }

        public Dictionary<string, object> StartGuidance(Dictionary<string, object> args)
        {
            EnsureFlight();
            if (!JsonUtil.Boolean(args, "confirm", false)) throw new KspMcpException("confirmation_required", "guidance start requires confirm=true", null);
            string profile = JsonUtil.String(args, "profile", "ascent").ToLowerInvariant();
            if (profile != "ascent" && profile != "landing" && profile != "orbit" && profile != "node_burn")
            {
                throw new KspMcpException("invalid_guidance_profile", "supported profiles are ascent, orbit, landing, and node_burn", profile);
            }
            double now = Planetarium.GetUniversalTime();
            double maxSeconds = Math.Max(5d, Math.Min(3600d, JsonUtil.Number(args, "max_seconds", profile == "landing" || profile == "node_burn" ? 600d : 240d)));
            _guidance = new GuidancePlan
            {
                Profile = profile,
                TargetApoapsis = Math.Max(1000d, JsonUtil.Number(args, "target_apoapsis", 80000d)),
                TargetPeriapsis = JsonUtil.Number(args, "target_periapsis", 75000d),
                TargetAltitude = Math.Max(10d, JsonUtil.Number(args, "target_altitude", 0d)),
                StartedAt = now,
                EndsAt = now + maxSeconds,
                AutoStage = JsonUtil.Boolean(args, "auto_stage", true),
                Phase = "initialising",
                LastError = null,
                BurnCompletionRecorded = false
            };
            try
            {
                if (profile == "node_burn") ConfigureNodeBurn(args, now);
            }
            catch
            {
                // Do not leave a half-configured controller active when a
                // node, engine, or timing precondition fails.
                _guidance = null;
                throw;
            }
            _lease.Clear();
            _leaseUntil = 0d;
            _sasEnabled = false;
            try { FireGroup("SAS", false); } catch (Exception) { }
            _lastGuidanceStageAt = now;
            return GuidanceStatus();
        }

        public Dictionary<string, object> StopGuidance()
        {
            bool wasActive = _guidance != null;
            _guidance = null;
            _lease.Clear();
            _leaseUntil = 0d;
            return new Dictionary<string, object> { { "stopped", wasActive }, { "guidance", GuidanceStatus() } };
        }

        public Dictionary<string, object> GuidanceStatus()
        {
            if (_guidance == null) return new Dictionary<string, object> { { "active", false } };
            double now = Planetarium.GetUniversalTime();
            return new Dictionary<string, object>
            {
                { "active", true },
                { "profile", _guidance.Profile },
                { "phase", _guidance.Phase },
                { "target_apoapsis", _guidance.TargetApoapsis },
                { "target_periapsis", _guidance.TargetPeriapsis },
                { "target_altitude", _guidance.TargetAltitude },
                { "auto_stage", _guidance.AutoStage },
                { "seconds_remaining", Math.Max(0d, _guidance.EndsAt - now) },
                { "last_throttle", _guidance.LastThrottle },
                { "last_pitch", _guidance.LastPitch },
                { "last_yaw", _guidance.LastYaw },
                { "target_pitch_degrees", _guidance.LastTargetPitchDegrees },
                { "burn_node_index", _guidance.BurnNodeIndex },
                { "burn_ut", _guidance.BurnUt },
                { "burn_duration", _guidance.BurnDuration },
                { "burn_throttle", _guidance.BurnThrottle },
                { "burn_delta_v", _guidance.BurnDeltaV },
                { "last_error", _guidance.LastError }
            };
        }

        private void ApplyGuidance(FlightCtrlState state)
        {
            Vessel vessel = _hookedVessel;
            if (vessel == null) return;
            if (_guidance.Profile == "landing")
            {
                ApplyLandingGuidance(state, vessel);
            }
            else if (_guidance.Profile == "node_burn")
            {
                ApplyNodeBurnGuidance(state, vessel);
            }
            else if (_guidance.Profile == "orbit")
            {
                ApplyOrbitGuidance(state, vessel);
            }
            else
            {
                ApplyAscentGuidance(state, vessel);
            }
        }

        private void ApplyAscentGuidance(FlightCtrlState state, Vessel vessel)
        {
            double altitude = Math.Max(0d, vessel.altitude);
            double apoapsis = vessel.orbit == null ? 0d : NumberMember(vessel.orbit, "ApA");
            double targetPitch = 90d;
            if (altitude >= 500d && altitude < 5000d) targetPitch = 90d - (altitude - 500d) / 4500d * 20d;
            else if (altitude >= 5000d && altitude < 20000d) targetPitch = 70d - (altitude - 5000d) / 15000d * 35d;
            else if (altitude >= 20000d && altitude < 35000d) targetPitch = 35d - (altitude - 20000d) / 15000d * 25d;
            else if (altitude >= 35000d) targetPitch = 10d;

            Vector3d target = AscentTargetVector(vessel, targetPitch);
            double throttle = 1d;
            if (apoapsis >= _guidance.TargetApoapsis)
            {
                throttle = 0d;
                _guidance.Phase = "coast_to_apoapsis";
                target = vessel.obt_velocity.sqrMagnitude > 0.01d ? vessel.obt_velocity.normalized : target;
            }
            else if (apoapsis > _guidance.TargetApoapsis * 0.85d)
            {
                throttle = ClampDouble((_guidance.TargetApoapsis - apoapsis) / Math.Max(1d, _guidance.TargetApoapsis * 0.15d), 0.15d, 1d);
                _guidance.Phase = "apoapsis_trim";
            }
            else if (altitude < 500d) _guidance.Phase = "vertical_rise";
            else if (altitude < 35000d) _guidance.Phase = "gravity_turn";
            else _guidance.Phase = "orbital_ascent";

            ApplyDirectionControl(state, vessel, target, throttle, targetPitch);
        }

        private void ConfigureNodeBurn(Dictionary<string, object> args, double now)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            ManeuverNode node = FindManeuverNode(vessel, Math.Max(0, JsonUtil.Integer(args, "node_index", 0)));
            if (node == null) throw new KspMcpException("maneuver_not_found", "requested maneuver node does not exist", null);
            double deltaV = node.DeltaV.magnitude;
            if (deltaV < 0.01d) throw new KspMcpException("maneuver_empty", "requested maneuver node has near-zero delta-v", null);
            double throttle = ClampDouble(JsonUtil.Number(args, "throttle", 1d), 0.1d, 1d);
            double thrust = NumberMember(vessel, "availableThrust");
            if (thrust <= 0d) thrust = EstimateActiveThrust(vessel);
            double mass = vessel.GetTotalMass();
            if (thrust <= 0d || mass <= 0d)
            {
                throw new KspMcpException("burn_engine_unavailable", "cannot estimate a burn without an active engine and positive vessel mass", null);
            }
            double acceleration = thrust / mass;
            double duration = Math.Max(0.5d, Math.Min(600d, deltaV / Math.Max(0.01d, acceleration * throttle) * 1.15d));
            if (node.UT < now - duration * 0.5d)
            {
                throw new KspMcpException("maneuver_in_past", "requested maneuver node is already too far in the past", new Dictionary<string, object> { { "ut", node.UT }, { "now", now } });
            }
            _guidance.BurnNodeIndex = Math.Max(0, JsonUtil.Integer(args, "node_index", 0));
            _guidance.BurnUt = node.UT;
            _guidance.BurnDuration = duration;
            _guidance.BurnThrottle = throttle;
            _guidance.BurnDeltaV = deltaV;
            _guidance.AutoStage = false;
            _guidance.Phase = "coast_to_node_burn";
            KspMcpBridge bridge = KspMcpBridge.Instance;
            if (bridge != null)
            {
                bridge.RecordEvent("flight.maneuver_burn.started", new Dictionary<string, object>
                {
                    { "node_index", _guidance.BurnNodeIndex },
                    { "ut", _guidance.BurnUt },
                    { "delta_v", _guidance.BurnDeltaV },
                    { "duration", _guidance.BurnDuration }
                });
            }
        }

        private void ApplyNodeBurnGuidance(FlightCtrlState state, Vessel vessel)
        {
            ManeuverNode node = FindManeuverNode(vessel, _guidance.BurnNodeIndex);
            if (node == null)
            {
                _guidance.LastError = "maneuver node disappeared before burn completion";
                _guidance.Phase = "node_missing";
                ApplyDirectionControl(state, vessel, vessel.obt_velocity, 0d, 0d);
                return;
            }
            Vector3d burnVector = vessel.orbit == null ? node.DeltaV : node.GetBurnVector(vessel.orbit);
            if (burnVector.sqrMagnitude < 0.0001d)
            {
                _guidance.LastError = "maneuver burn vector is empty";
                _guidance.Phase = "node_empty";
                ApplyDirectionControl(state, vessel, vessel.obt_velocity, 0d, 0d);
                return;
            }
            double now = Planetarium.GetUniversalTime();
            double start = _guidance.BurnUt - _guidance.BurnDuration * 0.5d;
            double end = _guidance.BurnUt + _guidance.BurnDuration * 0.5d;
            double throttle = 0d;
            if (now < start)
            {
                _guidance.Phase = "aligning_for_node_burn";
            }
            else if (now <= end)
            {
                throttle = _guidance.BurnThrottle;
                _guidance.Phase = "burning_node";
            }
            else
            {
                _guidance.Phase = "burn_complete";
                if (!_guidance.BurnCompletionRecorded)
                {
                    _guidance.BurnCompletionRecorded = true;
                    KspMcpBridge bridge = KspMcpBridge.Instance;
                    if (bridge != null) bridge.RecordEvent("flight.maneuver_burn.completed", new Dictionary<string, object>
                    {
                        { "node_index", _guidance.BurnNodeIndex },
                        { "ut", _guidance.BurnUt },
                        { "duration", _guidance.BurnDuration },
                        { "delta_v", _guidance.BurnDeltaV }
                    });
                }
            }
            ApplyDirectionControl(state, vessel, burnVector, throttle, 0d);
        }

        private static ManeuverNode FindManeuverNode(Vessel vessel, int index)
        {
            if (vessel == null || vessel.patchedConicSolver == null || vessel.patchedConicSolver.maneuverNodes == null) return null;
            if (index < 0 || index >= vessel.patchedConicSolver.maneuverNodes.Count) return null;
            return vessel.patchedConicSolver.maneuverNodes[index];
        }

        private static double EstimateActiveThrust(Vessel vessel)
        {
            if (vessel == null || vessel.parts == null) return 0d;
            double total = SumStageThrust(vessel, vessel.currentStage);
            if (total > 0d) return total;

            // Some editor-created vessels expose a stale currentStage value
            // immediately after launch. Fall back to the highest operational
            // engine stage instead of reporting that a perfectly good burn
            // has no engine.
            int fallbackStage = -1;
            foreach (Part part in vessel.parts)
            {
                if (part == null || part.Modules == null) continue;
                foreach (PartModule module in part.Modules)
                {
                    if (module == null || module.GetType().Name.IndexOf("ModuleEngines", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (!BoolMember(module, "isOperational") && !BoolMember(module, "moduleIsEnabled")) continue;
                    fallbackStage = Math.Max(fallbackStage, part.inverseStage);
                }
            }
            return fallbackStage < 0 ? 0d : SumStageThrust(vessel, fallbackStage);
        }

        private static double SumStageThrust(Vessel vessel, int stage)
        {
            double total = 0d;
            if (vessel == null || vessel.parts == null) return total;
            foreach (Part part in vessel.parts)
            {
                if (part == null || part.inverseStage != stage || part.Modules == null) continue;
                foreach (PartModule module in part.Modules)
                {
                    if (module == null || module.GetType().Name.IndexOf("ModuleEngines", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (!BoolMember(module, "isOperational") && !BoolMember(module, "moduleIsEnabled")) continue;
                    total += Math.Max(0d, NumberMember(module, "maxThrust"));
                }
            }
            return total;
        }

        public Dictionary<string, object> StartManeuverBurn(Dictionary<string, object> args)
        {
            var copy = new Dictionary<string, object>(args ?? new Dictionary<string, object>());
            copy["profile"] = "node_burn";
            return StartGuidance(copy);
        }

        private void ApplyOrbitGuidance(FlightCtrlState state, Vessel vessel)
        {
            double altitude = Math.Max(0d, vessel.altitude);
            double apoapsis = vessel.orbit == null ? 0d : NumberMember(vessel.orbit, "ApA");
            double periapsis = vessel.orbit == null ? -1d : NumberMember(vessel.orbit, "PeA");

            // A vessel launched straight into the orbit profile should still
            // receive the same safe gravity turn until KSP reports a useful
            // bound orbit. Once Ap/Pe exist, this controller switches to
            // burns at the appropriate apsis instead of repeating ascent.
            if (vessel.orbit == null || altitude < 1000d || apoapsis < 1000d)
            {
                ApplyAscentGuidance(state, vessel);
                if (_guidance != null) _guidance.Phase = "orbital_ascent";
                return;
            }

            double targetApoapsis = _guidance.TargetApoapsis;
            double targetPeriapsis = _guidance.TargetPeriapsis;
            double apoapsisTolerance = Math.Max(250d, targetApoapsis * 0.01d);
            double periapsisTolerance = Math.Max(250d, Math.Max(1d, targetPeriapsis) * 0.01d);
            double timeToApoapsis = NumberMember(vessel.orbit, "timeToAp");
            double timeToPeriapsis = NumberMember(vessel.orbit, "timeToPe");
            Vector3d prograde = vessel.obt_velocity.sqrMagnitude > 0.01d
                ? vessel.obt_velocity.normalized
                : AscentTargetVector(vessel, 0d);
            Vector3d retrograde = -prograde;
            double throttle = 0d;
            Vector3d target = prograde;
            double targetPitch = 0d;

            if (apoapsis < targetApoapsis - apoapsisTolerance)
            {
                double error = targetApoapsis - apoapsis;
                throttle = ClampDouble(error / Math.Max(1000d, targetApoapsis * 0.25d), 0.15d, 1d);
                target = prograde;
                targetPitch = 0d;
                _guidance.Phase = "raise_apoapsis";
            }
            else if (periapsis < targetPeriapsis - periapsisTolerance)
            {
                if (timeToApoapsis > 15d)
                {
                    _guidance.Phase = "coast_to_circularisation";
                }
                else
                {
                    double error = targetPeriapsis - periapsis;
                    throttle = ClampDouble(error / Math.Max(1000d, targetPeriapsis * 0.2d), 0.08d, 1d);
                    target = prograde;
                    _guidance.Phase = "raise_periapsis_at_apoapsis";
                }
            }
            else if (periapsis > targetPeriapsis + periapsisTolerance)
            {
                if (timeToPeriapsis > 15d)
                {
                    target = retrograde;
                    _guidance.Phase = "coast_to_lowering_burn";
                }
                else
                {
                    double error = periapsis - targetPeriapsis;
                    throttle = ClampDouble(error / Math.Max(1000d, targetPeriapsis * 0.2d), 0.08d, 1d);
                    target = retrograde;
                    _guidance.Phase = "lower_periapsis_at_periapsis";
                }
            }
            else
            {
                _guidance.Phase = "orbit_achieved";
            }

            ApplyDirectionControl(state, vessel, target, throttle, targetPitch);
        }

        private void ApplyLandingGuidance(FlightCtrlState state, Vessel vessel)
        {
            double altitude = Math.Max(0d, vessel.terrainAltitude);
            double verticalSpeed = vessel.verticalSpeed;
            double surfaceSpeed = vessel.srfSpeed;
            Vector3d velocity = vessel.srf_velocity;
            Vector3d retrograde = velocity.sqrMagnitude > 0.01d ? -velocity.normalized : SurfaceNormal(vessel);

            // Landing from orbit needs a deorbit decision before the
            // powered-descent loop. The old controller entered descent while
            // still on a positive periapsis and therefore never produced a
            // trajectory that intersected the body.
            double apoapsis = vessel.orbit == null ? 0d : NumberMember(vessel.orbit, "ApA");
            double periapsis = vessel.orbit == null ? -1d : NumberMember(vessel.orbit, "PeA");
            double timeToApoapsis = vessel.orbit == null ? 0d : NumberMember(vessel.orbit, "timeToAp");
            string situation = vessel.situation.ToString();
            bool landed = string.Equals(situation, "LANDED", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(situation, "SPLASHED", StringComparison.OrdinalIgnoreCase);
            if (!landed && altitude > 10000d && apoapsis > 20000d && periapsis > -1000d)
            {
                double throttle = 0d;
                if (timeToApoapsis > 15d)
                {
                    _guidance.Phase = "deorbit_coast_to_apoapsis";
                }
                else
                {
                    double error = periapsis + 1000d;
                    throttle = ClampDouble(error / Math.Max(5000d, Math.Abs(periapsis) + 10000d), 0.1d, 1d);
                    _guidance.Phase = "deorbit_burn";
                }
                ApplyDirectionControl(state, vessel, retrograde, throttle, 0d);
                return;
            }

            if (altitude > 30000d && verticalSpeed > -80d)
            {
                _guidance.Phase = "coast_to_entry_burn";
                ApplyDirectionControl(state, vessel, retrograde, 0d, 0d);
                return;
            }

            double mass = Math.Max(0.001d, vessel.GetTotalMass());
            double thrust = NumberMember(vessel, "availableThrust");
            if (thrust <= 0d) thrust = EstimateActiveThrust(vessel);
            double maxAcceleration = thrust <= 0d ? 0d : thrust / mass;
            double gravity = SurfaceGravity(vessel, altitude);
            double netBrakingAcceleration = Math.Max(0.1d, maxAcceleration - gravity);
            double downwardSpeed = Math.Max(0d, -verticalSpeed);
            double stoppingDistance = downwardSpeed * downwardSpeed / (2d * netBrakingAcceleration);
            double desiredVerticalSpeed = altitude > 5000d ? -30d : (altitude > 500d ? -10d : -2d);
            double verticalCorrection = Math.Max(0d, (desiredVerticalSpeed - verticalSpeed) * 0.5d);
            double horizontalBraking = surfaceSpeed * surfaceSpeed / Math.Max(20d, altitude * 2d);
            bool burnWindow = altitude <= stoppingDistance * 1.35d + 250d || verticalSpeed < desiredVerticalSpeed || altitude < 1000d;
            double requiredAcceleration = gravity + verticalCorrection;
            if (burnWindow) requiredAcceleration += horizontalBraking;
            double throttleCommand = maxAcceleration <= 0d ? 0d : ClampDouble(requiredAcceleration / maxAcceleration, 0d, 1d);

            if (landed || (altitude <= 5d && Math.Abs(verticalSpeed) < 2.5d && surfaceSpeed < 3d))
            {
                throttleCommand = 0d;
                _guidance.Phase = "landed_or_hovering";
            }
            else if (maxAcceleration <= gravity)
            {
                throttleCommand = maxAcceleration <= 0d ? 0d : 1d;
                _guidance.LastError = "available thrust is not greater than local gravity; controlled landing is not possible";
                _guidance.Phase = "insufficient_landing_thrust";
            }
            else if (!burnWindow) _guidance.Phase = "ballistic_descent";
            else if (altitude > 1000d) _guidance.Phase = "suicide_burn_and_retrograde_braking";
            else if (altitude > 200d) _guidance.Phase = "powered_descent";
            else _guidance.Phase = "final_hover_and_touchdown";
            ApplyDirectionControl(state, vessel, retrograde, throttleCommand, 0d);
        }

        private void ApplyDirectionControl(FlightCtrlState state, Vessel vessel, Vector3d target, double throttle, double targetPitch)
        {
            if (target.sqrMagnitude < 0.0001d) target = vessel.transform.forward;
            target.Normalize();
            Vector3 local = vessel.transform.InverseTransformDirection((Vector3)target);
            double forward = Math.Max(0.1d, local.z);
            double pitchError = Math.Atan2(local.y, forward);
            double yawError = Math.Atan2(local.x, forward);
            float pitch = Clamp((float)(pitchError * 2.2d), -1f, 1f);
            float yaw = Clamp((float)(yawError * 2.2d), -1f, 1f);
            state.mainThrottle = Clamp((float)throttle, 0f, 1f);
            state.pitch = pitch;
            state.yaw = yaw;
            state.roll = 0f;
            _guidance.LastThrottle = state.mainThrottle;
            _guidance.LastPitch = state.pitch;
            _guidance.LastYaw = state.yaw;
            _guidance.LastTargetPitchDegrees = targetPitch;
        }

        private void TryAutomaticStage(Vessel vessel)
        {
            if (_guidance == null || vessel == null || vessel.currentStage <= 0) return;
            _lastGuidanceStageAt = Planetarium.GetUniversalTime();
            bool currentStageHasEngine = false;
            bool currentStageLiveEngine = false;
            bool currentStageIgnited = false;
            bool anyIgnitedEngine = false;
            bool anyLiveEngine = false;
            foreach (Part part in vessel.parts)
            {
                if (part == null || part.Modules == null) continue;
                foreach (PartModule module in part.Modules)
                {
                    if (module == null || module.GetType().Name.IndexOf("ModuleEngines", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    bool flameout = BoolMember(module, "flameout");
                    bool operational = BoolMember(module, "isOperational");
                    bool ignited = BoolMember(module, "engineIgnited");
                    bool enabled = BoolMember(module, "moduleIsEnabled") || BoolMember(module, "isEnabled");
                    if (ignited) anyIgnitedEngine = true;
                    if (!flameout && (operational || ignited || enabled)) anyLiveEngine = true;
                    if (part.inverseStage != vessel.currentStage) continue;
                    currentStageHasEngine = true;
                    if (ignited) currentStageIgnited = true;
                    if (!flameout && (operational || ignited || enabled)) currentStageLiveEngine = true;
                }
            }
            // Do not gate ignition on speed.  A correctly staged vessel can
            // still be at (or very near) zero surface speed immediately
            // after launch, and waiting for motion here would leave the
            // engines disabled forever.
            if (currentStageHasEngine && !currentStageIgnited && _guidance.LastThrottle > 0.05d)
            {
                if (ActivateNextStageRaw())
                {
                    if (_guidance != null) _guidance.Phase = "automatic_ignition";
                    KspMcpBridge bridge = KspMcpBridge.Instance;
                    if (bridge != null) bridge.RecordEvent("flight.ignition.automatic", new Dictionary<string, object> { { "stage", vessel.currentStage } });
                }
                return;
            }
            // Once a previously activated stage is burning, its inverseStage
            // is usually greater than Vessel.currentStage because KSP has
            // already moved the staging cursor down. Inspect all engines
            // before deciding that a stage has died; otherwise an automated
            // controller can separate a healthy booster one frame later.
            if (anyIgnitedEngine && anyLiveEngine) return;
            if (!currentStageHasEngine && anyLiveEngine) return;
            if (currentStageHasEngine && currentStageLiveEngine && currentStageIgnited) return;
            try
            {
                Stage();
                if (_guidance != null) _guidance.Phase = "automatic_stage";
                KspMcpBridge bridge = KspMcpBridge.Instance;
                if (bridge != null) bridge.RecordEvent("flight.stage.automatic", new Dictionary<string, object> { { "stage", vessel.currentStage } });
            }
            catch (Exception exception)
            {
                if (_guidance != null) _guidance.LastError = exception.Message;
            }
        }

        private static bool ActivateNextStageRaw()
        {
            MethodInfo method = typeof(KSP.UI.Screens.StageManager).GetMethod("ActivateNextStage", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null) return false;
            try
            {
                method.Invoke(null, null);
                return true;
            }
            catch (Exception exception)
            {
                KspMcpBridge.Log("automatic ignition failed: " + exception.Message);
                return false;
            }
        }

        private static Vector3d AscentTargetVector(Vessel vessel, double pitchDegrees)
        {
            Vector3d up = SurfaceNormal(vessel);
            Vector3d east = SurfaceEasting(vessel);
            double radians = pitchDegrees * Math.PI / 180d;
            Vector3d result = up * Math.Sin(radians) + east * Math.Cos(radians);
            if (result.sqrMagnitude < 0.0001d) return up;
            result.Normalize();
            return result;
        }

        private static double SurfaceGravity(Vessel vessel, double altitude)
        {
            if (vessel == null || vessel.mainBody == null) return 9.80665d;
            double radius = Math.Max(1d, vessel.mainBody.Radius + Math.Max(0d, altitude));
            double mu = vessel.mainBody.gravParameter;
            if (mu <= 0d) return 9.80665d;
            return mu / (radius * radius);
        }

        private static Vector3d SurfaceNormal(Vessel vessel)
        {
            Vector3d fallback = vessel.GetWorldPos3D();
            if (vessel.mainBody != null) fallback -= (Vector3d)vessel.mainBody.transform.position;
            if (fallback.sqrMagnitude < 0.0001d) fallback = new Vector3d(0d, 1d, 0d);
            fallback.Normalize();
            return InvokeBodyVector(vessel, "GetSurfaceNVector", fallback);
        }

        private static Vector3d SurfaceEasting(Vessel vessel)
        {
            Vector3d fallback = Vector3d.Cross(new Vector3d(0d, 1d, 0d), SurfaceNormal(vessel));
            if (fallback.sqrMagnitude < 0.0001d) fallback = new Vector3d(1d, 0d, 0d);
            fallback.Normalize();
            return InvokeBodyVector(vessel, "GetSurfaceEasting", fallback);
        }

        private static Vector3d InvokeBodyVector(Vessel vessel, string methodName, Vector3d fallback)
        {
            if (vessel == null || vessel.mainBody == null) return fallback;
            try
            {
                MethodInfo method = vessel.mainBody.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (method != null)
                {
                    object value = method.Invoke(vessel.mainBody, new object[] { vessel.latitude, vessel.longitude });
                    if (value is Vector3d)
                    {
                        Vector3d result = (Vector3d)value;
                        if (result.sqrMagnitude > 0.0001d) { result.Normalize(); return result; }
                    }
                }
            }
            catch (Exception) { }
            return fallback;
        }

        public Dictionary<string, object> SetSas(Dictionary<string, object> args)
        {
            EnsureFlight();
            _sasEnabled = JsonUtil.Boolean(args, "enabled", false);
            FireGroup("SAS", _sasEnabled);
            return new Dictionary<string, object> { { "sas", _sasEnabled }, { "state", Snapshot() } };
        }

        public Dictionary<string, object> SetRcs(Dictionary<string, object> args)
        {
            EnsureFlight();
            _rcsEnabled = JsonUtil.Boolean(args, "enabled", false);
            FireGroup("RCS", _rcsEnabled);
            return new Dictionary<string, object> { { "rcs", _rcsEnabled }, { "state", Snapshot() } };
        }

        public Dictionary<string, object> Stage()
        {
            EnsureFlight();
            Vessel vessel = FlightGlobals.ActiveVessel;
            int before = vessel.currentStage;
            if (before <= 0) throw new KspMcpException("no_next_stage", "the vessel is already at its final staging index", before);
            int target = Math.Max(0, before - 1);
            List<Part> stagedParts = new List<Part>();
            foreach (Part part in vessel.parts)
            {
                if (part != null && part.inverseStage == target) stagedParts.Add(part);
            }

            // StageManager is reliable for stock decouplers, but it does not
            // consistently invoke ModuleEnginesFX actions on vessels created
            // through the editor API. Invoke the actual KSPAction first, then
            // move the current-stage cursor ourselves. This preserves the
            // normal highest-to-lowest staging order while making engine
            // ignition deterministic for MCP-built vessels.
            bool invoked = false;
            foreach (Part part in stagedParts)
            {
                if (InvokeStagedPart(part, target, vessel)) invoked = true;
            }

            if (invoked || stagedParts.Count == 0)
            {
                TrySetCurrentStage(vessel, target);
                KspMcpBridge bridge = KspMcpBridge.Instance;
                if (bridge != null) bridge.RecordEvent("flight.stage.activated", new Dictionary<string, object>
                {
                    { "stage_before", before },
                    { "stage_activated", target },
                    { "custom_activation", invoked }
                });
                return new Dictionary<string, object>
                {
                    { "staged", true },
                    { "stage_before", before },
                    { "stage_activated", target },
                    { "custom_activation", invoked },
                    { "state", Snapshot() }
                };
            }

            MethodInfo method = typeof(KSP.UI.Screens.StageManager).GetMethod("ActivateNextStage", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null) throw new KspMcpException("stage_unavailable", "KSP StageManager.ActivateNextStage is not available", null);
            method.Invoke(null, null);
            KspMcpBridge bridgeAfter = KspMcpBridge.Instance;
            if (bridgeAfter != null) bridgeAfter.RecordEvent("flight.stage.activated", new Dictionary<string, object>
            {
                { "stage_before", before },
                { "stage_activated", target },
                { "custom_activation", false }
            });
            return new Dictionary<string, object> { { "staged", true }, { "stage_before", before }, { "state", Snapshot() } };
        }

        public Dictionary<string, object> Warp(Dictionary<string, object> args)
        {
            int index = JsonUtil.Integer(args, "rate_index", 0);
            if (index < 0) index = 0;
            MethodInfo[] methods = typeof(TimeWarp).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            foreach (MethodInfo method in methods)
            {
                if (method.Name != "SetRate") continue;
                ParameterInfo[] parameters = method.GetParameters();
                try
                {
                    if (parameters.Length == 2 && parameters[0].ParameterType == typeof(int) && parameters[1].ParameterType == typeof(bool))
                    {
                        method.Invoke(null, new object[] { index, true });
                        return new Dictionary<string, object> { { "rate_index", index }, { "set", true } };
                    }
                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(int))
                    {
                        method.Invoke(null, new object[] { index });
                        return new Dictionary<string, object> { { "rate_index", index }, { "set", true } };
                    }
                }
                catch (Exception exception)
                {
                    throw new KspMcpException("warp_failed", exception.Message, null);
                }
            }
            throw new KspMcpException("warp_unavailable", "KSP TimeWarp.SetRate is not available", null);
        }

        public Dictionary<string, object> Bodies(Dictionary<string, object> args)
        {
            string query = JsonUtil.String(args, "query", "").ToLowerInvariant();
            var result = new List<object>();
            if (FlightGlobals.Bodies != null)
            {
                foreach (CelestialBody body in FlightGlobals.Bodies)
                {
                    if (body == null) continue;
                    string name = body.bodyName ?? "";
                    if (query.Length > 0 && name.ToLowerInvariant().IndexOf(query, StringComparison.Ordinal) < 0) continue;
                    result.Add(new Dictionary<string, object>
                    {
                        { "name", name },
                        { "display_name", body.theName },
                        { "radius_m", body.Radius },
                        { "grav_parameter_m3_s2", body.gravParameter },
                        { "sphere_of_influence_m", body.sphereOfInfluence },
                        { "atmosphere", body.atmosphere },
                        { "max_atmosphere_altitude_m", NumberMember(body, "atmosphereDepth") },
                        { "ocean", body.ocean },
                        { "reference_body", body.referenceBody == null ? null : body.referenceBody.bodyName },
                        { "orbit", body.orbit == null ? null : OrbitSummary(body.orbit) }
                    });
                }
            }
            return new Dictionary<string, object> { { "count", result.Count }, { "bodies", result } };
        }

        public Dictionary<string, object> ManeuverNodes()
        {
            EnsureFlight();
            Vessel vessel = FlightGlobals.ActiveVessel;
            var nodes = new List<object>();
            if (vessel.patchedConicSolver != null && vessel.patchedConicSolver.maneuverNodes != null)
            {
                foreach (ManeuverNode node in vessel.patchedConicSolver.maneuverNodes)
                {
                    if (node == null) continue;
                    Vector3d deltaV = node.DeltaV;
                    Vector3d burnVector = vessel.orbit == null ? deltaV : node.GetBurnVector(vessel.orbit);
                    nodes.Add(new Dictionary<string, object>
                    {
                        { "ut", node.UT },
                        { "eta", node.UT - Planetarium.GetUniversalTime() },
                        { "radial_plus_mps", deltaV.x },
                        { "normal_plus_mps", -deltaV.y },
                        { "prograde_mps", deltaV.z },
                        { "delta_v_mps", Math.Sqrt(deltaV.sqrMagnitude) },
                        { "delta_v_node_coordinates", JsonUtil.Vector3dObject(deltaV) },
                        { "burn_vector_world", JsonUtil.Vector3dObject(burnVector) },
                        { "orbit_after", node.nextPatch == null ? null : OrbitSummary(node.nextPatch) }
                    });
                }
            }
            return new Dictionary<string, object> { { "count", nodes.Count }, { "nodes", nodes } };
        }

        public Dictionary<string, object> AddManeuverNode(Dictionary<string, object> args)
        {
            EnsureFlight();
            if (!JsonUtil.Boolean(args, "confirm", false)) throw new KspMcpException("confirmation_required", "adding a maneuver node requires confirm=true", null);
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel.patchedConicSolver == null) throw new KspMcpException("maneuver_unavailable", "active vessel has no patched conic solver", null);
            double defaultUt = Planetarium.GetUniversalTime() + (vessel.orbit == null ? 60d : Math.Max(1d, vessel.orbit.timeToAp));
            double ut = Math.Max(Planetarium.GetUniversalTime() + 0.1d, JsonUtil.Number(args, "ut", defaultUt));
            double radial = JsonUtil.Number(args, "radial", 0d);
            double normalPlus = JsonUtil.Number(args, "normal", 0d);
            double prograde = JsonUtil.Number(args, "prograde", 0d);
            ManeuverNode node = vessel.patchedConicSolver.AddManeuverNode(ut);
            if (node == null) throw new KspMcpException("maneuver_failed", "KSP did not return a maneuver node", null);
            node.OnGizmoUpdated(new Vector3d(radial, -normalPlus, prograde), ut);
            KspMcpBridge bridge = KspMcpBridge.Instance;
            if (bridge != null) bridge.RecordEvent("flight.maneuver_node.added", new Dictionary<string, object> { { "ut", ut }, { "prograde", prograde }, { "normal", normalPlus }, { "radial", radial } });
            return new Dictionary<string, object> { { "added", true }, { "node", ManeuverNodes() } };
        }

        public Dictionary<string, object> ClearManeuverNodes(Dictionary<string, object> args)
        {
            EnsureFlight();
            if (!JsonUtil.Boolean(args, "confirm", false)) throw new KspMcpException("confirmation_required", "clearing maneuver nodes requires confirm=true", null);
            Vessel vessel = FlightGlobals.ActiveVessel;
            int count = 0;
            if (vessel.patchedConicSolver != null && vessel.patchedConicSolver.maneuverNodes != null)
            {
                var nodes = new List<ManeuverNode>(vessel.patchedConicSolver.maneuverNodes);
                foreach (ManeuverNode node in nodes)
                {
                    if (node == null) continue;
                    try { vessel.patchedConicSolver.RemoveManeuverNode(node); } catch (Exception) { try { node.RemoveSelf(); } catch (Exception) { } }
                    count++;
                }
            }
            return new Dictionary<string, object> { { "cleared", count }, { "nodes", ManeuverNodes() } };
        }

        public Dictionary<string, object> ActivatePart(Dictionary<string, object> args)
        {
            EnsureFlight();
            Part part = null;
            string id = JsonUtil.String(args, "part_id", null);
            if (!string.IsNullOrEmpty(id)) part = _craft.FindPartForControl(id);
            if (part == null && JsonUtil.Has(args, "flight_id"))
            {
                uint flightId = (uint)Math.Max(0, JsonUtil.Integer(args, "flight_id", 0));
                Vessel vessel = FlightGlobals.ActiveVessel;
                if (vessel != null) part = vessel[flightId];
            }
            if (part == null) throw new KspMcpException("part_not_found", "flight part not found", null);
            string eventName = JsonUtil.String(args, "event", null);
            if (string.IsNullOrEmpty(eventName))
            {
                bool activated = false;
                try { activated = part.activate(part.inverseStage, FlightGlobals.ActiveVessel); }
                catch (Exception exception) { KspMcpBridge.Log("part.activate failed: " + exception.Message); }
                bool engineInvoked = InvokeEngineModules(part);
                bool actionInvoked = false;
                if (!engineInvoked) actionInvoked = InvokePartAction(part, "ActivateAction");
                if (!activated && !engineInvoked && !actionInvoked) part.force_activate();
            }
            else if (eventName.EndsWith("Action", StringComparison.OrdinalIgnoreCase))
            {
                if (!InvokePartAction(part, eventName)) part.SendEvent(eventName);
            }
            else part.SendEvent(eventName);
            return new Dictionary<string, object> { { "activated", true }, { "part_id", id }, { "event", eventName } };
        }

        private static bool InvokeStagedPart(Part part, int stage, Vessel vessel)
        {
            if (part == null) return false;
            bool partActivated = false;
            try
            {
                partActivated = part.activate(stage, vessel);
            }
            catch (Exception exception)
            {
                KspMcpBridge.Log("part.activate stage failed: " + exception.Message);
            }
            if (part.Modules == null) return false;
            foreach (PartModule module in part.Modules)
            {
                if (module == null) continue;
                string moduleName = module.GetType().Name;
                if (moduleName.IndexOf("ModuleEngines", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return InvokeEngineModule(module) || InvokePartAction(part, "ActivateAction") || partActivated;
                }
                if (moduleName.IndexOf("ModuleDecouple", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    moduleName.IndexOf("ModuleAnchoredDecoupler", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    part.SendEvent("Decouple");
                    return true;
                }
            }
            return partActivated;
        }

        private static bool InvokeEngineModules(Part part)
        {
            if (part == null || part.Modules == null) return false;
            foreach (PartModule module in part.Modules)
            {
                if (module == null || module.GetType().Name.IndexOf("ModuleEngines", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (InvokeEngineModule(module)) return true;
            }
            return false;
        }

        private static bool InvokeEngineModule(PartModule module)
        {
            ModuleEngines engine = module as ModuleEngines;
            if (engine == null) return false;
            try
            {
                engine.Activate();
                KspMcpBridge.Log("direct engine Activate invoked module=" + module.GetType().Name);
                return true;
            }
            catch (Exception exception)
            {
                KspMcpBridge.Log("direct engine Activate failed: " + exception.GetType().Name + ": " + exception.Message);
                return false;
            }
        }

        private static bool InvokePartAction(Part part, string actionName)
        {
            if (part == null || part.Modules == null || string.IsNullOrEmpty(actionName)) return false;
            foreach (PartModule module in part.Modules)
            {
                if (module == null || module.Actions == null) continue;
                BaseAction action = module.Actions[actionName];
                if (action == null) continue;
                action.Invoke(new KSPActionParam(KSPActionGroup.None, KSPActionType.Activate));
                InvokeEngineModule(module);
                return true;
            }
            return false;
        }

        private static bool TrySetCurrentStage(Vessel vessel, int stage)
        {
            if (vessel == null) return false;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            try
            {
                FieldInfo field = vessel.GetType().GetField("currentStage", flags);
                if (field != null && field.FieldType == typeof(int))
                {
                    field.SetValue(vessel, stage);
                    return true;
                }
                PropertyInfo property = vessel.GetType().GetProperty("currentStage", flags);
                MethodInfo setter = property == null ? null : property.GetSetMethod(true);
                if (setter != null)
                {
                    setter.Invoke(vessel, new object[] { stage });
                    return true;
                }
            }
            catch (Exception exception)
            {
                KspMcpBridge.Log("could not set current stage: " + exception.Message);
            }
            return false;
        }

        public Dictionary<string, object> Abort()
        {
            EnsureFlight();
            FireGroup("Abort", true);
            return new Dictionary<string, object> { { "abort_fired", true }, { "state", Snapshot() } };
        }

        public Dictionary<string, object> ClearControl()
        {
            _lease.Clear();
            _leaseUntil = 0d;
            return new Dictionary<string, object> { { "control_released", true }, { "controls", ControlSnapshot() } };
        }

        public Dictionary<string, object> Recover()
        {
            MethodInfo[] methods = typeof(FlightDriver).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            foreach (string name in new[] { "RevertToPrelaunch", "RevertToLaunch", "RecoverVessel" })
            {
                foreach (MethodInfo method in methods)
                {
                    if (method.Name != name || method.GetParameters().Length != 0) continue;
                    method.Invoke(null, null);
                    return new Dictionary<string, object> { { "recover_requested", true }, { "method", name } };
                }
            }
            throw new KspMcpException("recover_unavailable", "KSP does not expose a supported recovery method in this version", null);
        }

        public Dictionary<string, object> Snapshot()
        {
            Vessel vessel = null;
            try { vessel = FlightGlobals.ActiveVessel; } catch (Exception) { }
            if (vessel == null)
            {
                return new Dictionary<string, object> { { "available", false }, { "control_lease", ControlSnapshot() } };
            }

            var result = new Dictionary<string, object>
            {
                { "available", true },
                { "vessel_name", vessel.vesselName },
                { "vessel_id", vessel.id.ToString() },
                { "active", vessel.isActiveVessel },
                { "loaded", vessel.loaded },
                { "universal_time", Planetarium.GetUniversalTime() },
                { "commandable", vessel.isCommandable },
                { "situation", vessel.situation.ToString() },
                { "body", vessel.mainBody == null ? null : vessel.mainBody.bodyName },
                { "mission_time", vessel.missionTime },
                { "altitude", vessel.altitude },
                { "terrain_altitude", vessel.terrainAltitude },
                { "surface_speed", vessel.srfSpeed },
                { "orbital_speed", vessel.obt_speed },
                { "vertical_speed", vessel.verticalSpeed },
                { "latitude", vessel.latitude },
                { "longitude", vessel.longitude },
                { "mass_tonnes", vessel.GetTotalMass() },
                { "current_stage", vessel.currentStage },
                { "position", JsonUtil.Vector3dObject(vessel.GetWorldPos3D()) },
                { "velocity", JsonUtil.Vector3dObject(vessel.obt_velocity) },
                { "orientation", JsonUtil.QuaternionObject(vessel.transform.rotation) },
                { "controls", CurrentControlState(vessel.ctrlState) },
                { "control_lease", ControlSnapshot() },
                { "guidance", GuidanceStatus() },
                { "resources", AggregateResources(vessel) },
                { "engines", EngineSnapshot(vessel) }
            };

            if (vessel.orbit != null)
            {
                result["orbit"] = new Dictionary<string, object>
                {
                    { "apoapsis", NumberMember(vessel.orbit, "ApA") },
                    { "periapsis", NumberMember(vessel.orbit, "PeA") },
                    { "eccentricity", NumberMember(vessel.orbit, "eccentricity") },
                    { "inclination", NumberMember(vessel.orbit, "inclination") },
                    { "period", NumberMember(vessel.orbit, "period") },
                    { "time_to_apoapsis", NumberMember(vessel.orbit, "timeToAp") },
                    { "time_to_periapsis", NumberMember(vessel.orbit, "timeToPe") }
                };
            }
            return result;
        }

        /// <summary>
        /// A deliberately small telemetry record for high-rate polling.
        /// Snapshot() is intentionally detailed for inspection, but it walks
        /// every resource and engine module. Sending that payload every frame
        /// made a visionless MCP client both slow and unnecessarily noisy.
        /// </summary>
        public Dictionary<string, object> CompactSnapshot()
        {
            Vessel vessel = null;
            try { vessel = FlightGlobals.ActiveVessel; } catch (Exception) { }
            if (vessel == null)
            {
                return new Dictionary<string, object>
                {
                    { "available", false },
                    { "control_lease", ControlSnapshot() }
                };
            }

            var result = new Dictionary<string, object>
            {
                { "available", true },
                { "vessel_name", vessel.vesselName },
                { "vessel_id", vessel.id.ToString() },
                { "active", vessel.isActiveVessel },
                { "loaded", vessel.loaded },
                { "commandable", vessel.isCommandable },
                { "situation", vessel.situation.ToString() },
                { "body", vessel.mainBody == null ? null : vessel.mainBody.bodyName },
                { "universal_time", Planetarium.GetUniversalTime() },
                { "mission_time", vessel.missionTime },
                { "altitude", vessel.altitude },
                { "terrain_altitude", vessel.terrainAltitude },
                { "surface_speed", vessel.srfSpeed },
                { "orbital_speed", vessel.obt_speed },
                { "vertical_speed", vessel.verticalSpeed },
                { "latitude", vessel.latitude },
                { "longitude", vessel.longitude },
                { "mass_tonnes", vessel.GetTotalMass() },
                { "current_stage", vessel.currentStage },
                { "position", JsonUtil.Vector3dObject(vessel.GetWorldPos3D()) },
                { "velocity", JsonUtil.Vector3dObject(vessel.obt_velocity) },
                { "orientation", JsonUtil.QuaternionObject(vessel.transform.rotation) },
                { "controls", CurrentControlState(vessel.ctrlState) },
                { "control_lease", ControlSnapshot() },
                { "guidance", GuidanceStatus() },
                { "engine_summary", CompactEngineSummary(vessel) },
                { "resources", CompactResourceSummary(vessel) }
            };

            if (vessel.orbit != null)
            {
                result["orbit"] = new Dictionary<string, object>
                {
                    { "apoapsis", NumberMember(vessel.orbit, "ApA") },
                    { "periapsis", NumberMember(vessel.orbit, "PeA") },
                    { "eccentricity", NumberMember(vessel.orbit, "eccentricity") },
                    { "inclination", NumberMember(vessel.orbit, "inclination") },
                    { "period", NumberMember(vessel.orbit, "period") },
                    { "time_to_apoapsis", NumberMember(vessel.orbit, "timeToAp") },
                    { "time_to_periapsis", NumberMember(vessel.orbit, "timeToPe") }
                };
            }
            return result;
        }

        private Dictionary<string, object> CompactEngineSummary(Vessel vessel)
        {
            RefreshCompactSummary(vessel);
            return _compactEngineSummary ?? EngineSummary(vessel);
        }

        private List<object> CompactResourceSummary(Vessel vessel)
        {
            RefreshCompactSummary(vessel);
            return _compactResources ?? AggregateResources(vessel);
        }

        private void RefreshCompactSummary(Vessel vessel)
        {
            if (vessel == null) return;
            float now = Time.realtimeSinceStartup;
            string vesselId = vessel.id.ToString();
            if (_compactEngineSummary != null &&
                string.Equals(_compactSummaryVesselId, vesselId, StringComparison.Ordinal) &&
                _lastCompactSummaryAt >= 0f && now - _lastCompactSummaryAt < 0.25f) return;
            _compactSummaryVesselId = vesselId;
            _lastCompactSummaryAt = now;
            _compactEngineSummary = EngineSummary(vessel);
            _compactResources = AggregateResources(vessel);
        }

        private void EnsureFlight()
        {
            if (FlightGlobals.ActiveVessel == null) throw new KspMcpException("no_active_vessel", "KSP has no active vessel", KspMcpBridge.SceneName());
        }

        private void FireGroup(string groupName, bool activate)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) return;
            KSPActionGroup group;
            if (!TryParseGroup(groupName, out group)) throw new KspMcpException("invalid_action_group", "unknown action group: " + groupName, null);
            MethodInfo[] methods = typeof(BaseAction).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            KSPActionType actionType = activate ? KSPActionType.Activate : KSPActionType.Deactivate;
            foreach (MethodInfo method in methods)
            {
                if (method.Name != "FireAction") continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 4)
                {
                    method.Invoke(null, new object[] { vessel.parts, group, -1, actionType });
                    return;
                }
                if (parameters.Length == 3)
                {
                    method.Invoke(null, new object[] { vessel.parts, group, actionType });
                    return;
                }
            }
            throw new KspMcpException("action_unavailable", "KSP BaseAction.FireAction is not available", null);
        }

        private static bool TryParseGroup(string name, out KSPActionGroup group)
        {
            string normalised = name;
            if (string.Equals(name, "Lights", StringComparison.OrdinalIgnoreCase)) normalised = "Light";
            try
            {
                group = (KSPActionGroup)Enum.Parse(typeof(KSPActionGroup), normalised, true);
                return true;
            }
            catch (Exception)
            {
                group = KSPActionGroup.None;
                return false;
            }
        }

        private Dictionary<string, object> ControlSnapshot()
        {
            return new Dictionary<string, object>
            {
                { "active", _leaseUntil > 0d && Planetarium.GetUniversalTime() <= _leaseUntil },
                { "seconds_remaining", Math.Max(0d, _leaseUntil - Planetarium.GetUniversalTime()) },
                { "sas", _sasEnabled },
                { "rcs", _rcsEnabled },
                { "throttle", _lease.HasThrottle ? (object)_lease.Throttle : null },
                { "pitch", _lease.HasPitch ? (object)_lease.Pitch : null },
                { "yaw", _lease.HasYaw ? (object)_lease.Yaw : null },
                { "roll", _lease.HasRoll ? (object)_lease.Roll : null }
            };
        }

        private static Dictionary<string, object> CurrentControlState(FlightCtrlState state)
        {
            return new Dictionary<string, object>
            {
                { "throttle", state.mainThrottle },
                { "pitch", state.pitch },
                { "yaw", state.yaw },
                { "roll", state.roll },
                { "translate_x", state.X },
                { "translate_y", state.Y },
                { "translate_z", state.Z },
                { "sas", state.killRot },
                { "gear_up", state.gearUp },
                { "gear_down", state.gearDown },
                { "lights", state.headlight }
            };
        }

        private static Dictionary<string, object> OrbitSummary(Orbit orbit)
        {
            if (orbit == null) return null;
            return new Dictionary<string, object>
            {
                { "reference_body", orbit.referenceBody == null ? null : orbit.referenceBody.bodyName },
                { "semi_major_axis_m", NumberMember(orbit, "semiMajorAxis") },
                { "apoapsis_m", NumberMember(orbit, "ApA") },
                { "periapsis_m", NumberMember(orbit, "PeA") },
                { "eccentricity", NumberMember(orbit, "eccentricity") },
                { "inclination_deg", NumberMember(orbit, "inclination") },
                { "longitude_of_ascending_node_deg", NumberMember(orbit, "LAN") },
                { "argument_of_periapsis_deg", NumberMember(orbit, "argumentOfPeriapsis") },
                { "period_s", NumberMember(orbit, "period") },
                { "epoch_ut", NumberMember(orbit, "epoch") },
                { "time_to_apoapsis_s", NumberMember(orbit, "timeToAp") },
                { "time_to_periapsis_s", NumberMember(orbit, "timeToPe") }
            };
        }

        private static List<object> AggregateResources(Vessel vessel)
        {
            var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var maximums = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (Part part in vessel.parts)
            {
                if (part == null || part.Resources == null) continue;
                foreach (PartResource resource in part.Resources)
                {
                    if (resource == null) continue;
                    if (!totals.ContainsKey(resource.resourceName)) totals[resource.resourceName] = 0d;
                    if (!maximums.ContainsKey(resource.resourceName)) maximums[resource.resourceName] = 0d;
                    totals[resource.resourceName] += resource.amount;
                    maximums[resource.resourceName] += resource.maxAmount;
                }
            }
            var result = new List<object>();
            foreach (KeyValuePair<string, double> item in totals)
            {
                result.Add(new Dictionary<string, object>
                {
                    { "name", item.Key }, { "amount", item.Value }, { "max_amount", maximums[item.Key] }
                });
            }
            return result;
        }

        private static List<object> EngineSnapshot(Vessel vessel)
        {
            var result = new List<object>();
            foreach (Part part in vessel.parts)
            {
                if (part == null || part.Modules == null) continue;
                foreach (PartModule module in part.Modules)
                {
                    if (module == null || module.GetType().Name.IndexOf("ModuleEngines", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    result.Add(new Dictionary<string, object>
                    {
                        { "part_flight_id", part.flightID },
                        { "module", module.GetType().Name },
                        { "max_thrust", NumberMember(module, "maxThrust") },
                        { "final_thrust", NumberMember(module, "finalThrust") },
                        { "ignited", BoolMember(module, "engineIgnited") },
                        { "flameout", BoolMember(module, "flameout") },
                        { "operational", BoolMember(module, "isOperational") },
                        { "enabled", BoolMember(module, "isEnabled") },
                        { "module_enabled", BoolMember(module, "moduleIsEnabled") },
                        { "staged", BoolMember(module, "staged") },
                        { "requested_throttle", NumberMember(module, "requestedThrottle") },
                        { "current_throttle", NumberMember(module, "currentThrottle") },
                        { "propellant_requirement", NumberMember(module, "propellantReqMet") }
                    });
                }
            }
            return result;
        }

        private static Dictionary<string, object> EngineSummary(Vessel vessel)
        {
            int count = 0;
            int ignited = 0;
            int operational = 0;
            int flameout = 0;
            int enabled = 0;
            int staged = 0;
            double maxThrust = 0d;
            double finalThrust = 0d;
            if (vessel != null && vessel.parts != null)
            {
                foreach (Part part in vessel.parts)
                {
                    if (part == null || part.Modules == null) continue;
                    foreach (PartModule module in part.Modules)
                    {
                        if (module == null || module.GetType().Name.IndexOf("ModuleEngines", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        count++;
                        if (BoolMember(module, "engineIgnited")) ignited++;
                        if (BoolMember(module, "isOperational")) operational++;
                        if (BoolMember(module, "flameout")) flameout++;
                        if (BoolMember(module, "moduleIsEnabled") || BoolMember(module, "isEnabled")) enabled++;
                        if (BoolMember(module, "staged")) staged++;
                        maxThrust += Math.Max(0d, NumberMember(module, "maxThrust"));
                        finalThrust += Math.Max(0d, NumberMember(module, "finalThrust"));
                    }
                }
            }
            return new Dictionary<string, object>
            {
                { "count", count },
                { "ignited", ignited },
                { "operational", operational },
                { "flameout", flameout },
                { "enabled", enabled },
                { "staged", staged },
                { "max_thrust_kN", maxThrust },
                { "final_thrust_kN", finalThrust }
            };
        }

        private static double NumberMember(object target, string name)
        {
            try
            {
                Type type = target.GetType();
                FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                object value = field == null ? null : field.GetValue(target);
                if (value == null)
                {
                    PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    value = property == null ? null : property.GetValue(target, null);
                }
                if (value is IConvertible) return Convert.ToDouble(value);
            }
            catch (Exception) { }
            return 0d;
        }

        private static bool BoolMember(object target, string name)
        {
            try
            {
                Type type = target.GetType();
                FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                object value = field == null ? null : field.GetValue(target);
                if (value == null)
                {
                    PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    value = property == null ? null : property.GetValue(target, null);
                }
                return value is bool && (bool)value;
            }
            catch (Exception) { return false; }
        }

        private static void SetMember(object target, string name, object value)
        {
            try
            {
                Type type = target.GetType();
                FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null)
                {
                    if (field.FieldType == typeof(float)) field.SetValue(target, Convert.ToSingle(value));
                    else if (field.FieldType == typeof(bool)) field.SetValue(target, Convert.ToBoolean(value));
                    return;
                }
                PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (property != null && property.CanWrite)
                {
                    if (property.PropertyType == typeof(float)) property.SetValue(target, Convert.ToSingle(value), null);
                    else if (property.PropertyType == typeof(bool)) property.SetValue(target, Convert.ToBoolean(value), null);
                }
            }
            catch (Exception) { }
        }

        private static float Clamp(float value, float min, float max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        private static double ClampDouble(double value, double min, double max)
        {
            return Math.Max(min, Math.Min(max, value));
        }
    }
}

Authored by GPT-6 at the request of @genereddick.
- Co-authored by GPT-6. Simulation implementation.
- Co-authored by GPT-6. Independent review.
- Co-authored by GPT-6 (Codex).

# FDT AR Foundation patches

This fork starts from the upstream `6.6.2` tag, commit `4f9da2a631b9f868b676a9cf9fa6edf09de7608b`, in the public Unity package mirror. The package version is `6.6.2-fdt.3`. Keep the upstream license and copyright notices.

## Persistent XR Simulation sessions

A Single scene load destroys XR Simulation's local-physics environment. A persistent AR session keeps its initialized scanner, which still references the destroyed environment root. Starting plane detection in the destination then throws `MissingReferenceException` from `SimulationEnvironmentScanner.CreateMeshColliders`.

The patch rebuilds a missing environment during scene completion, before destination `Start`. The session also checks its environment when a stopped session starts again. The scanner retains its identity, native buffers, and registered consumers. Mesh/input subsystems and other providers retain their identities. A healthy environment survives an additive load without a reset.

Image and bounding-box discovery re-enumerate a replacement environment. Delivered old value IDs become removals; additions that no consumer received do not. Delayed discovery work belongs to its environment generation. Editor mesh publication coalesces setup/reset requests and drops obsolete roots or subsystem instances.

Each changed production region is marked `FDT patch`. Physical ARKit provider code, loader selection, product scenes, and device defaults are unchanged.

## Allocation and lifetime review

Scene observation owns one cached delegate per simulation provider. The Editor mesh publisher owns one cached callback per mesh helper. Image discovery retains one delivery record per environment image because destroyed Unity components cannot provide removal IDs. Removal buffers retain capacity and grow only when outstanding removals plus the next environment require it. These reservations avoid new closures, repeated metadata inspection, and loader/manager reconstruction.

Environment instantiation and the existing vendor discovery/mesh generation paths allocate. Native scan buffers are reused. Review measured allocation evidence separately; this patch makes no zero-allocation or device frame-time claim.

## Validation

Use the target application's exact installed Editor, its XR Simulation loader, and an isolated PlayMode scene. Add `com.unity.xr.arfoundation` to that test project's `testables` to expose the package fixtures. Run `UnityEngine.XR.Simulation.Tests.FdtSimulationLifecycleTests` in PlayMode. The fixture performs three Single loads and checks destination Start, the retained scanner, live native mesh/input identities, a disabled consumer, additive loading, and restarting a stopped session after scene replacement.

Run `SimulationImageTrackingTests`, `SimulationEnvironmentTestFixture`, and `SimulationCameraTestFixture` for adjacent vendor behavior. The ViewScreenMaster companion fixture `XrSimulationSceneTransitionTests` exercises its actual persistent-rig installer callback and keeps the real `ARMeshManager` and `ARInputManager` references live.

Windows native Editor execution does not establish Mac Editor or compiled-player behavior. Mac reproduction, ARKit device smoke checks, and any new use of XR Simulation in standalone players need separate evidence.

## Added trackable initialization


The manager invokes both its own hook and the added trackable's hook before publishing changes, matching its existing update path. Previously it skipped the trackable hook on additions. Static simulated probes therefore had valid provider descriptors but no initialized custom cubemap or placement type. ARKit can also deliver an initial addition without an immediate update.

The fix adds one virtual callback per added trackable. It adds no collection, closure, texture copy, timer, or per-frame analysis. The existing trackable initializer may create its owned vendor texture wrapper and configure renderer state at first discovery; that intended work is no longer deferred until a later update. This changes a shared lifecycle seam, so device first-frame behavior still needs hardware coverage.

Run `FdtAddedProbeInitializationTests` in a fresh graphics-enabled PlayMode process. The first addition must expose a valid descriptor, custom cubemap, and automatic placement type in the change notification. Idle frames must retain the same wrapper. The ViewScreenMaster companion `XrSimulationProbeScanTests` additionally checks the actual Scan sender, publication, and stop/restart.

Manual registration also sets the provider data before registering the trackable with the spawner. Registering under the initial invalid ID retained the wrong entry and could assert when the next Scan helper was created. This adds one struct assignment before registration; the existing pose/application path remains intact. `FdtManualProbeRegistrationTests` checks two distinct provider identities without a native subsystem.

## Environment probe ownership and inactive prefabs

Review of the first-addition fix found two lifetime gaps. ReflectionProbe dependencies can be initialized before Awake without activating an inactive debug prefab. Such a never-active object does not receive Unity's OnDestroy callback, so it records geometry/placement and defers owned wrapper creation until Awake. Activation applies the retained provider data without requiring another provider update. Already-awakened probes retain their wrapper while disabled.

Destruction disposes the probe's owned updatable texture and calls the base trackable destruction path. It clears the renderer assignment only when it still references that owned texture. Foreign assigned textures are not destroyed. Clearing a renderer assignment no longer creates a second wrapper; ownership remains with the retained updatable texture. Metadata changes still follow the vendor's existing replacement behavior.

These changes add no timer, pooling layer, capture, copy, or recurring allocation. Existing vendor wrapper creation remains an explicit framework boundary. Editor identity/destruction assertions do not establish native ARKit provider retention through asynchronous GPU copies, native-memory peaks, or device frame times.

Run `AddedProbe_InitializesItsTextureBeforeChanged_AndRetainsItWhileIdle` and `InactiveDebugPrefab_PublishesDataWithoutActivation_ThenInitializesOnActivation` in separate fresh graphics-enabled processes. Run `FdtEnvironmentProbeTextureLifetimeTests`, `FdtManualProbeRegistrationTests`, and `TrackableSpawnerTests` with graphics. Repeat with a Null device: rendering cases must explicitly ignore, while registration/spawner cases execute. The lifetime fixture checks owned Unity texture destruction without UnloadUnusedAssets, retained identity after clearing an assignment, and survival of borrowed Unity texture objects. It does not measure native GPU memory or prove native provider leases.

Never-active probes also skip the base trackable destruction callback. Provider removal, pending removal, and probe-manager teardown now release their spawner registrations explicitly. Deferred callbacks match the registered instance so a reused provider ID is safe. Objects explicitly retained with destroyOnRemoval=false keep their existing registration and lifetime. The removal path adds dictionary lookups and value-type keys, with no new collection, delegate, polling, or texture work.

Run `FdtTrackableRemovalTests` with graphics or Null. Its synthetic provider drives the real manager Update/removal path and checks externally destroyed inactive probes, deferred destruction, provider ID reuse, retained objects, and actual environment-probe-manager teardown before any global ResetInstance. The texture lifetime fixture also checks pending removal and reuse before resetting the spawner.

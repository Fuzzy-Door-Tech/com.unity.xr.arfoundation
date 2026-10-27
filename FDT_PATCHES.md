Authored by GPT-6 at the request of @genereddick.
- Co-authored by GPT-6.

# FDT AR Foundation patches

This fork starts from the upstream `6.6.2` tag, commit `4f9da2a631b9f868b676a9cf9fa6edf09de7608b`, in the public Unity package mirror. The package version is `6.6.2-fdt.1`. Keep the upstream license and copyright notices.

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

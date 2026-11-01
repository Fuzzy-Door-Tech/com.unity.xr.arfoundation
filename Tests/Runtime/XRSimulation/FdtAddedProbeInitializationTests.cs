// FDT patch: added environment probes initialize before observers receive them.
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using Unity.XR.CoreUtils;
using UnityEngine.Events;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.Management;
using UnityEngine.XR.Simulation;

namespace UnityEngine.XR.Simulation.Tests
{
    public sealed class FdtAddedProbeInitializationTests
    {
        private const string k_EnvironmentPrefabPath = "Packages/com.unity.xr.arfoundation/Assets/Prefabs/DefaultSimulationEnvironment.prefab";

        private GameObject _rig;
        private XRManagerSettings _loader;
        private bool _ownsLoader;
        private System.Func<SimulationSceneManager> _previousSceneManagerFactory;
        private AREnvironmentProbeManager _probes;
        private UnityAction<ARTrackablesChangedEventArgs<AREnvironmentProbe>> _changed;
        private int _added;
        private AREnvironmentProbe _first;
        private Texture _texture;
        private GameObject _inactivePrefab;
        private bool _inactiveCase;
        private bool _ownsSceneFactory;

        [UnityTest]
        [Explicit("Requires a fresh PlayMode process. Run this fixture alone.")]
        public IEnumerator AddedProbe_InitializesItsTextureBeforeChanged_AndRetainsItWhileIdle() => Exercise(false);

        [UnityTest]
        [Explicit("Requires a fresh PlayMode process. Run this method alone.")]
        public IEnumerator InactiveDebugPrefab_PublishesDataWithoutActivation_ThenInitializesOnActivation() => Exercise(true);

        private IEnumerator Exercise(bool inactive)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("XR Simulation renders cubemaps and requires a graphics device.");
            Assert.That(Object.FindAnyObjectByType<ARSession>(FindObjectsInactive.Include), Is.Null,
                "Run in a fresh PlayMode process without an existing AR rig.");
            _inactiveCase = inactive;
            _added = 0;
            _first = null;
            _texture = null;
            _previousSceneManagerFactory = SimulationSessionSubsystem.simulationSceneManagerFactory;
            _ownsSceneFactory = true;
            SimulationSessionSubsystem.simulationSceneManagerFactory = () =>
                new SimulationTestSceneManager(k_EnvironmentPrefabPath);
            _loader = XRGeneralSettings.Instance.Manager;
            Assert.That(_loader, Is.Not.Null);
            _ownsLoader = !_loader.isInitializationComplete;
            if (_ownsLoader) _loader.InitializeLoaderSync();
            Assert.That(_loader.activeLoader, Is.InstanceOf<SimulationLoader>());
            _rig = new GameObject("Added probe initialization fixture");
            _rig.SetActive(false);
            var origin = _rig.AddComponent<XROrigin>();
            var cameraObject = new GameObject("Simulation camera", typeof(Camera));
            cameraObject.transform.SetParent(_rig.transform, false);
            cameraObject.AddComponent<UnityEngine.InputSystem.XR.TrackedPoseDriver>();
            origin.Camera = cameraObject.GetComponent<Camera>();
            origin.CameraFloorOffsetObject = cameraObject;
            _rig.AddComponent<ARSession>();
            _rig.AddComponent<ARInputManager>();
            _probes = _rig.AddComponent<AREnvironmentProbeManager>();
            _probes.automaticPlacementRequested = true;
            if (inactive)
            {
                _inactivePrefab = new GameObject("Inactive probe debug template");
                _inactivePrefab.SetActive(false);
                _inactivePrefab.AddComponent<AREnvironmentProbe>();
                _probes.debugPrefab = _inactivePrefab;
            }
            _changed = OnChanged;
            _probes.trackablesChanged.AddListener(_changed);
            _rig.SetActive(true);
            var deadline = Time.realtimeSinceStartupAsDouble + 15.0;
            while (_added == 0 && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.That(_added, Is.GreaterThan(0));
            if (inactive)
            {
                Assert.That(_first.gameObject.activeSelf, Is.False, "Discovery must not activate the debug template instance.");
                Assert.That(_first.GetComponent<ReflectionProbe>().customBakedTexture, Is.Null);
                _first.gameObject.SetActive(true);
                Assert.That(_first.textureDescriptor.valid, Is.True, "Activation uses retained data without another provider update.");
                _texture = _first.GetComponent<ReflectionProbe>().customBakedTexture;
            }
            Assert.That(_texture, Is.Not.Null);
            for (var frame = 0; frame < 5; frame++) yield return null;
            Assert.That(_first.GetComponent<ReflectionProbe>().customBakedTexture, Is.SameAs(_texture));
            LogAssert.NoUnexpectedReceived();
        }

        private void OnChanged(ARTrackablesChangedEventArgs<AREnvironmentProbe> changes)
        {
            foreach (var probe in changes.added)
            {
                _added++;
                Assert.That(probe.textureDescriptor.valid, Is.EqualTo(!_inactiveCase),
                    "Active probes initialize before notification; inactive probes defer ownership until Awake.");
                Assert.That(probe.placementType, Is.EqualTo(AREnvironmentProbePlacementType.Automatic));
                var texture = probe.GetComponent<ReflectionProbe>().customBakedTexture;
                if (_inactiveCase) Assert.That(texture, Is.Null);
                else Assert.That(texture, Is.Not.Null);
                if (_first) continue;
                _first = probe;
                _texture = texture;
            }
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_probes && _changed != null) _probes.trackablesChanged.RemoveListener(_changed);
            if (_rig) Object.Destroy(_rig);
            if (_inactivePrefab) Object.Destroy(_inactivePrefab);
            yield return null;
            if (_ownsLoader && _loader) _loader.DeinitializeLoader();
            _ownsLoader = false;
            if (_ownsSceneFactory) SimulationSessionSubsystem.simulationSceneManagerFactory = _previousSceneManagerFactory;
            _ownsSceneFactory = false;
            _inactivePrefab = null;
            _previousSceneManagerFactory = null;
            _loader = null;
            _rig = null;
        }
    }
}
#endif

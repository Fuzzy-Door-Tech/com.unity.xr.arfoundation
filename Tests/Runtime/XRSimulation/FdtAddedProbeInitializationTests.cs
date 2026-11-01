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

        [UnityTest]
        [Explicit("Requires a fresh PlayMode process. Run this fixture alone.")]
        public IEnumerator AddedProbe_InitializesItsTextureBeforeChanged_AndRetainsItWhileIdle()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("XR Simulation renders cubemaps and requires a graphics device.");
            Assert.That(Object.FindAnyObjectByType<ARSession>(FindObjectsInactive.Include), Is.Null,
                "Run in a fresh PlayMode process without an existing AR rig.");
            _previousSceneManagerFactory = SimulationSessionSubsystem.simulationSceneManagerFactory;
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
            _changed = OnChanged;
            _probes.trackablesChanged.AddListener(_changed);
            _rig.SetActive(true);
            var deadline = Time.realtimeSinceStartupAsDouble + 15.0;
            while (_added == 0 && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.That(_added, Is.GreaterThan(0));
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
                Assert.That(probe.textureDescriptor.valid, Is.True, "The first notification must expose initialized texture data.");
                Assert.That(probe.placementType, Is.EqualTo(AREnvironmentProbePlacementType.Automatic));
                var texture = probe.GetComponent<ReflectionProbe>().customBakedTexture;
                Assert.That(texture, Is.Not.Null);
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
            yield return null;
            if (_ownsLoader && _loader) _loader.DeinitializeLoader();
            _ownsLoader = false;
            SimulationSessionSubsystem.simulationSceneManagerFactory = _previousSceneManagerFactory;
            _previousSceneManagerFactory = null;
            _loader = null;
            _rig = null;
        }
    }
}
#endif

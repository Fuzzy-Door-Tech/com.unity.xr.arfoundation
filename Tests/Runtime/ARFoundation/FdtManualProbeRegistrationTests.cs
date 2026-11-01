// FDT patch: manual trackables enter the spawner with their provider identity.
using System;
using NUnit.Framework;
using Unity.XR.CoreUtils;
using UnityEngine.XR.ARSubsystems;

namespace UnityEngine.XR.ARFoundation.Tests
{
    public sealed class FdtManualProbeRegistrationTests
    {
        private GameObject _root;

        [Test]
        public void TwoManualProbes_RegisterTheirProviderIds_WithoutAnInvalidIdEntry()
        {
            _root = new GameObject("Manual registration fixture");
            var origin = _root.AddComponent<XROrigin>();
            var spawner = TrackableSpawner.instance;
            spawner.SetOrigin(origin);
            var manager = _root.AddComponent<FdtManualProbeTestManager>();
            for (var i = 0; i < 2; i++)
            {
                var go = new GameObject("Manual probe");
                go.transform.SetParent(_root.transform, false);
                var probe = go.AddComponent<AREnvironmentProbe>();
                var id = new TrackableId(77, (ulong)i + 1);
                var data = new XREnvironmentProbe(id, Vector3.one, Pose.identity, Vector3.one,
                    default, TrackingState.Tracking, IntPtr.Zero);
                manager.AddExisting(probe, data);
                Assert.That(probe.trackableId, Is.EqualTo(id));
                Assert.That(spawner.m_EntriesByTrackableId.ContainsKey(id), Is.True);
                Assert.That(spawner.m_EntriesByTrackableId.ContainsKey(TrackableId.invalidId), Is.False);
            }
            Assert.That(spawner.m_EntriesByTrackableId.Count, Is.EqualTo(2));
        }

        [TearDown]
        public void TearDown()
        {
            if (_root) Object.DestroyImmediate(_root);
            TrackableSpawner.ResetInstance();
        }
    }

    public sealed class FdtManualProbeTestManager : ARTrackableManager<XREnvironmentProbeSubsystem,
        XREnvironmentProbeSubsystemDescriptor, XREnvironmentProbeSubsystem.Provider, XREnvironmentProbe, AREnvironmentProbe>
    {
        protected override string gameObjectName => "Manual probe";
        // The registration seam does not require starting an XR provider.
        protected override void OnEnable() { }
        protected override void OnDisable() { }
        public void AddExisting(AREnvironmentProbe probe, XREnvironmentProbe data) => CreateTrackableFromExisting(probe, data);
    }
}

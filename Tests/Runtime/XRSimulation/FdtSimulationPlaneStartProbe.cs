// FDT patch: prove that a destination's Start can use the environment without a delayed repair.
using UnityEngine.XR.ARSubsystems;

namespace UnityEngine.XR.Simulation.Tests
{
    public sealed class FdtSimulationPlaneStartProbe : MonoBehaviour
    {
        public static bool StartPlanes;
        public static int StartCount;
        public static XRPlaneSubsystem PlaneSubsystem;

        void Start()
        {
            if (!StartPlanes) return;
            PlaneSubsystem.Start();
            StartCount++;
        }
    }
}

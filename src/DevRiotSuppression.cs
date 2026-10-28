using System;
using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;

namespace CivicPressure
{
    internal static class DevRiotSuppression
    {
        internal static volatile string Status = "";
    }
    internal static class DevSuppressionRules
    {
        internal static bool Enabled(bool active, bool riot, bool police, int arrived) { return active && riot && police && arrived > 0; }
        internal static bool Contact(bool ready, float distanceSquared) { return ready && distanceSquared <= 3.5f * 3.5f; }
        internal static float Progress(float time, float start, float duration) { return Math.Max(0, Math.Min(1, (time - start) / duration)); }
        // Follow the existing suppression deadline, including pause, speed changes and Real Time.
        internal static float Elapsed(double remaining)
        { return double.IsNaN(remaining) || double.IsInfinity(remaining) ? 0 : (float)Math.Max(0, Math.Min(24, (30 - remaining) * .8)); }
        internal static int Phase(float time) { return time < 3 ? 0 : time < 5 ? 1 : time < 12 ? 2 : time < 20 ? 3 : 4; }
        internal static float Gas(float time) { return Progress(time, 5, 1) * (1 - Progress(time, 12, 4)); }
        internal static float Fall(float time, int officer)
        { return officer % 3 == 0 ? Progress(time, 13, .45f) * (1 - Progress(time, 16, 1)) : 0; }
        internal static float Strike(float time, int officer)
        {
            if (time < 12 || time >= 20 || Fall(time, officer) > .15f) return 0;
            float cycle = (time - 12 + officer * .32f) % 2.4f;
            return (float)Math.Sin(Progress(cycle, .6f, .55f) * Math.PI);
        }
        internal static float Recoil(float time, int officer)
        {
            if (time < 12 || time >= 20 || Fall(time, officer) > .15f) return 0;
            float cycle = (time - 12 + officer * .32f) % 2.4f;
            return (float)Math.Sin(Progress(cycle, .85f, 1.1f) * Math.PI);
        }
    }
    internal sealed class DevPoliceUnitView
    {
        internal ushort Vehicle;
        internal Vector3 Position;
        internal int Side;
        internal bool Armed;
    }
    internal static partial class PoliceDeployment
    {
        // Simulation-thread snapshot: no main-thread reads of mutable deployment/vehicle lists.
        internal static DevPoliceUnitView[] DevArrived(UnrestEvent e)
        {
            List<DevPoliceUnitView> result = new List<DevPoliceUnitView>();
            if (e != null && e.Riot && e.Police)
                foreach (PoliceUnit unit in Units(e))
                {
                    
                    if (!Valid(e, unit)) continue;
                    Vehicle car = Singleton<VehicleManager>.instance.m_vehicles.m_buffer[unit.Vehicle];
                    if ((car.m_flags & (Vehicle.Flags.Spawned | Vehicle.Flags.Stopped | Vehicle.Flags.Deleted)) != (Vehicle.Flags.Spawned | Vehicle.Flags.Stopped) || !DevIsParked(unit.Vehicle)) continue;
                    result.Add(new DevPoliceUnitView { Vehicle = unit.Vehicle, Position = car.GetLastFramePosition(), Side=DevSide(unit.Vehicle), Armed=DevArmedCars.ContainsKey(unit.Vehicle) });
                }
            return result.ToArray();
        }
    }

    public sealed partial class CrowdPreview
    {
        private bool devSuppressing;
        private long devRiotStamp;
        private float devTime;
        private Vector3 devForward, devCenter;
        private bool devGrenadeThrown;
        private float devThrowTime;
        private Vector3 devThrowOrigin, devGasLanding;
        private GameObject devCanister, devGasRoot;
        private ParticleSystem devGas;
        private Material devCanisterMaterial, devGasMaterial;
        private Texture2D devGasTexture;
        private bool devEffectsFailed;
        private void DevSuppressionEffects()
        {
            if(devEffectsFailed) return;
            try { DevSuppressionEffectsCore(); }
            catch(Exception error) {
                devEffectsFailed=true;
                if(devCanister!=null) devCanister.SetActive(false);
                if(devGasRoot!=null) devGasRoot.SetActive(false);
                Debug.LogWarning("[CivicPressure] Optional suppression particles disabled; crowd continues: "+error);
            }
        }
        private void DevSuppressionEffectsCore()
        {
            if (!devSuppressing) return;
            if (!devGrenadeThrown) { DevHideSuppressionEffects(); return; }
            if (devCanister == null)
            {
                devCanister = GameObject.CreatePrimitive(PrimitiveType.Cylinder); devCanister.name = "CivicPressure_TearGasCanister";
                devCanister.transform.parent = transform; devCanister.transform.localScale = new Vector3(.14f, .16f, .14f);
                Collider collider = devCanister.GetComponent<Collider>(); collider.enabled = false; UnityEngine.Object.Destroy(collider);
                devCanisterMaterial = new Material(Shader.Find("Diffuse")); devCanisterMaterial.color = new Color(.25f, .3f, .27f);
                devCanister.GetComponent<Renderer>().sharedMaterial = devCanisterMaterial;
                DevCreateGas();
            }
            float flight = DevSuppressionRules.Progress(devTime, devThrowTime, 2);
            devCanister.transform.position = Vector3.Lerp(devThrowOrigin, devGasLanding, flight) + Vector3.up * ((float)Math.Sin(flight * Math.PI) * 2.2f);
            devCanister.transform.rotation = Quaternion.Euler(flight * 720, 0, 90);
            devCanister.SetActive(devTime < devThrowTime + 3);
            devGasRoot.transform.position = devGasLanding;
            bool emit = devTime >= devThrowTime + 2 && devTime < devThrowTime + 9;
            ParticleSystem.EmissionModule emission = devGas.emission; emission.enabled = emit;
            float speed = Singleton<SimulationManager>.instance.m_simulationTimeSpeed;
            ParticleSystem.MainModule main = devGas.main; main.simulationSpeed = speed;
            if (speed < .00001f) devGas.Pause(false);
            else if (emit && !devGas.isPlaying) devGas.Play(false);
            else if (devGas.isPaused) devGas.Play(false);
        }
        private void DevCreateGas()
        {
            devGasRoot = new GameObject("CivicPressure_TearGasSmoke"); devGasRoot.transform.parent = transform;
            devGas = devGasRoot.AddComponent<ParticleSystem>(); devGas.Stop(false); devGas.Clear(false);
            Shader shader = Shader.Find("Particles/Alpha Blended");
            if(shader==null) {
                FireEffect native=Singleton<BuildingManager>.instance.m_properties.m_fireEffect as FireEffect;
                ParticleSystemRenderer renderer=native!=null && native.m_particleEffect!=null ? native.m_particleEffect.GetComponent<ParticleSystemRenderer>() : null;
                if(renderer!=null && renderer.sharedMaterial!=null) shader=renderer.sharedMaterial.shader;
            }
            if (shader == null) throw new InvalidOperationException("No native particle shader available");
            devGasMaterial = new Material(shader);
            devGasTexture = new Texture2D(32, 32, TextureFormat.ARGB32, false);
            for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++)
            {
                float dx = (x - 15.5f) / 15.5f, dy = (y - 15.5f) / 15.5f;
                float edge = Math.Max(0, 1 - dx * dx - dy * dy);
                devGasTexture.SetPixel(x, y, new Color(1, 1, 1, edge * edge));
            }
            devGasTexture.wrapMode = TextureWrapMode.Clamp; devGasTexture.Apply(); devGasMaterial.mainTexture = devGasTexture;
            devGas.GetComponent<ParticleSystemRenderer>().sharedMaterial = devGasMaterial;
            ParticleSystem.MainModule main = devGas.main;
            main.loop = true; main.playOnAwake = false; main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = 3.5f; main.startSpeed = .25f; main.startSize = new ParticleSystem.MinMaxCurve(1.5f, 2.5f);
            main.startColor = new Color(.72f, .76f, .69f, .15f); main.maxParticles = 64;
            ParticleSystem.ShapeModule shape = devGas.shape; shape.shapeType = ParticleSystemShapeType.Sphere; shape.radius = 1.1f;
            ParticleSystem.EmissionModule emission = devGas.emission; emission.rateOverTime = 10; emission.enabled = false;
            ParticleSystem.SizeOverLifetimeModule size = devGas.sizeOverLifetime; size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1, AnimationCurve.Linear(0, .5f, 1, 1.6f));
            Gradient gradient = new Gradient();
            gradient.SetKeys(new GradientColorKey[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
                new GradientAlphaKey[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(1, .2f), new GradientAlphaKey(.5f, .8f), new GradientAlphaKey(0, 1) });
            ParticleSystem.ColorOverLifetimeModule color = devGas.colorOverLifetime; color.enabled = true; color.color = new ParticleSystem.MinMaxGradient(gradient);
        }
        private void DevHideSuppressionEffects()
        {
            DevHideLines();
            if (devCanister != null) devCanister.SetActive(false);
            if (devGas != null) { devGas.Stop(false); devGas.Clear(false); }
        }
        private void DevSuppressionReset()
        {
            DevHideSuppressionEffects(); devSuppressing=false; devRiotStamp=0; devTime=0;
            devGrenadeThrown=false; devThrowTime=0; devEffectsFailed=false;
            DevResetCombat();
            foreach(CrowdActor actor in people) { actor.SetPlacardVisible(true); actor.HideBaton(); actor.CombatWeapon(Quaternion.identity,false,false,false,0); }
            foreach(CrowdActor officer in officers) officer.HideBaton();
            DevRiotSuppression.Status="";
        }
        private void DevDisposeSuppression()
        {
            DevSuppressionReset();
            if (devCanister != null) UnityEngine.Object.Destroy(devCanister);
            if (devGasRoot != null) UnityEngine.Object.Destroy(devGasRoot);
            if (devCanisterMaterial != null) UnityEngine.Object.Destroy(devCanisterMaterial);
            if (devGasMaterial != null) UnityEngine.Object.Destroy(devGasMaterial);
            if (devGasTexture != null) UnityEngine.Object.Destroy(devGasTexture);
        }
    }
}

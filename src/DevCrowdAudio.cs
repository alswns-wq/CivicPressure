using System;
using System.IO;
using ColossalFramework;
using UnityEngine;

namespace CivicPressure
{
    internal static class DevCrowdAudioRules
    {
        internal const int Voices = 4;
        internal static float Gain(float distance)
        { return distance <= 40 ? 1 : distance >= 450 ? 0 : (450-distance)/410; }
        internal static float[] Decode(byte[] pcm)
        {
            if(pcm==null || pcm.Length==0 || pcm.Length%2!=0 || pcm.Length>22050*2*30)
                throw new InvalidDataException("Invalid bundled mono PCM audio");
            float[] samples=new float[pcm.Length/2];
            for(int i=0;i<samples.Length;i++)
                samples[i]=(short)(pcm[i*2] | pcm[i*2+1]<<8)/32768f;
            return samples;
        }
    }

    public sealed partial class CrowdPreview
    {
        private AudioClip devCrowdClip, devShotClip, devLiveShotClip, devImpactClip;
        private AudioSource devCrowdLoop;
        private readonly AudioSource[] devSoundVoices=new AudioSource[DevCrowdAudioRules.Voices];
        private readonly Vector3[] devSoundPositions=new Vector3[DevCrowdAudioRules.Voices];
        private readonly float[] devSoundLevels=new float[DevCrowdAudioRules.Voices];
        private readonly SavedFloat devEffectsVolume=new SavedFloat(Settings.effectAudioVolume,
            Settings.gameSettingsFile,DefaultSettings.effectAudioVolume,true);
        private bool devAudioFailed, devAudioAllowed;
        private float devAudioVolume, devNextSound, devAudioCenterAt;
        private Vector3 devAudioCenter;
        private Camera devAudioCamera;

        private AudioClip DevLoadAudio(string name)
        {
            byte[] bytes;
            using(Stream stream=typeof(CrowdPreview).Assembly.GetManifestResourceStream("CivicPressure.Audio."+name)) {
                if(stream==null) throw new InvalidDataException("Missing audio: "+name);
                using(BinaryReader reader=new BinaryReader(stream)) bytes=reader.ReadBytes((int)stream.Length);
            }
            float[] samples=DevCrowdAudioRules.Decode(bytes);
            AudioClip clip=AudioClip.Create("Civic Pressure "+name,samples.Length,1,22050,false);
            if(!clip.SetData(samples,0)) { Destroy(clip); throw new InvalidDataException("Audio upload failed: "+name); }
            return clip;
        }
        private AudioSource DevAudioSource(string name)
        {
            GameObject node=new GameObject(name); node.transform.parent=transform;
            AudioSource source=node.AddComponent<AudioSource>();
            source.playOnAwake=false; source.spatialBlend=0; source.dopplerLevel=0;
            // Game audio uses camera-relative attenuation; do not depend on a differently placed listener.
            source.volume=0; return source;
        }
        private void DevPrepareAudio()
        {
            if(devCrowdLoop!=null) return;
            devCrowdClip=DevLoadAudio("crowd.pcm"); devShotClip=DevLoadAudio("shot.pcm"); devImpactClip=DevLoadAudio("impact.pcm");
            devLiveShotClip=DevLoadAudio("live-shot.pcm");
            devCrowdLoop=DevAudioSource("Civic Pressure crowd audio");
            devCrowdLoop.clip=devCrowdClip; devCrowdLoop.loop=true;
            for(int i=0;i<devSoundVoices.Length;i++) devSoundVoices[i]=DevAudioSource("Civic Pressure action audio");
        }
        private void DevStopAudio()
        {
            devAudioAllowed=false;
            if(devCrowdLoop!=null) devCrowdLoop.Stop();
            for(int i=0;i<devSoundVoices.Length;i++) if(devSoundVoices[i]!=null) devSoundVoices[i].Stop();
        }
        private void DevAudioTick(UnrestView state,bool paused,Camera camera)
        {
            if(devAudioFailed) return;
            try {
                AudioManager audio=Singleton<AudioManager>.instance;
                devAudioCamera=camera;
                devAudioVolume=Mathf.Clamp01(audio.MasterVolume)*Mathf.Clamp01(devEffectsVolume.value);
                devAudioAllowed=state.Active && !paused && camera!=null && !audio.MuteAll &&
                    !Singleton<LoadingManager>.instance.m_currentlyLoading && devAudioVolume>0;
                if(!devAudioAllowed) { DevStopAudio(); return; }
                if(Time.realtimeSinceStartup>=devAudioCenterAt) {
                    devAudioCenterAt=Time.realtimeSinceStartup+.2f;
                    Vector3 sum=Vector3.zero; int count=0;
                    if(state.Riot) for(int i=0;i<devRioters.Length;i++) {
                        DevCombatant person=devRioters[i];
                        if(person!=null && (person.Gone || person.Detained || person.Fleeing)) continue;
                        if(devRiotPlaced[i]) { sum+=person!=null && DevCombatControls(i) ? person.Position : devRiotPlaces[i]; count++; }
                    }
                    devAudioCenter=count>0 ? sum/count : state.Position;
                }
                float gain=DevCrowdAudioRules.Gain(Vector3.Distance(camera.transform.position,devAudioCenter));
                if(devCrowdLoop==null && gain<=0) return;
                DevPrepareAudio();
                // One ambience for the entire crowd; never one loop per citizen.
                float target=devAudioVolume*gain*(state.Riot ? .576f : .336f);
                if(state.Riot && devBattleOutcome>0) target=0;
                devCrowdLoop.volume=Mathf.MoveTowards(devCrowdLoop.volume,target,Time.unscaledDeltaTime*.7f);
                if(target>0 && !devCrowdLoop.isPlaying) devCrowdLoop.Play();
                else if(target==0 && devCrowdLoop.volume==0) devCrowdLoop.Stop();
                for(int i=0;i<devSoundVoices.Length;i++) if(devSoundVoices[i].isPlaying)
                    DevPositionSound(devSoundVoices[i],devSoundPositions[i],devSoundLevels[i]);
            } catch(Exception error) { DevAudioError(error); }
        }
        private void DevPositionSound(AudioSource source,Vector3 position,float level)
        {
            Vector3 relative=devAudioCamera.transform.InverseTransformPoint(position);
            source.volume=devAudioVolume*level*DevCrowdAudioRules.Gain(relative.magnitude);
            source.panStereo=Mathf.Clamp(relative.x/Math.Max(40,relative.magnitude),-.7f,.7f);
        }
        private void DevPoliceSound(Vector3 position,bool shot,bool liveAmmo)
        {
            if(devAudioFailed || !devAudioAllowed || devAudioCamera==null || Time.realtimeSinceStartup<devNextSound) return;
            if(DevCrowdAudioRules.Gain(Vector3.Distance(devAudioCamera.transform.position,position))<=0) return;
            try {
                DevPrepareAudio();
                for(int i=0;i<devSoundVoices.Length;i++) {
                    AudioSource source=devSoundVoices[i]; if(source.isPlaying) continue;
                    devSoundPositions[i]=position; devSoundLevels[i]=shot ? .84f : .66f;
                    source.clip=shot ? (liveAmmo ? devLiveShotClip : devShotClip) : devImpactClip;
                    DevPositionSound(source,position,devSoundLevels[i]); source.Play();
                    devNextSound=Time.realtimeSinceStartup+.12f; return;
                }
            } catch(Exception error) { DevAudioError(error); }
        }
        private void DevAudioError(Exception error)
        {
            devAudioFailed=true; DevDisposeAudio();
            Debug.LogError("[CivicPressure] DEV audio disabled; visuals and combat continue: "+error);
        }
        private void DevDisposeAudio()
        {
            DevStopAudio();
            if(devCrowdLoop!=null) Destroy(devCrowdLoop.gameObject); devCrowdLoop=null;
            for(int i=0;i<devSoundVoices.Length;i++) {
                if(devSoundVoices[i]!=null) Destroy(devSoundVoices[i].gameObject); devSoundVoices[i]=null;
            }
            if(devCrowdClip!=null) Destroy(devCrowdClip); devCrowdClip=null;
            if(devShotClip!=null) Destroy(devShotClip); devShotClip=null;
            if(devLiveShotClip!=null) Destroy(devLiveShotClip); devLiveShotClip=null;
            if(devImpactClip!=null) Destroy(devImpactClip); devImpactClip=null;
        }
        public void OnDisable() { DevStopAudio(); }
    }
}

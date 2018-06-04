﻿using Barotrauma.Extensions;
using Barotrauma.Items.Components;
using Barotrauma.Sounds;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Barotrauma
{
    public struct DamageSound
    {
        //the range of inflicted damage where the sound can be played
        //(10.0f, 30.0f) would be played when the inflicted damage is between 10 and 30
        public readonly Vector2 damageRange;

        public readonly string damageType;

        public readonly Sound sound;

        public readonly string requiredTag;

        public DamageSound(Sound sound, Vector2 damageRange, string damageType, string requiredTag = "")
        {
            this.sound = sound;
            this.damageRange = damageRange;
            this.damageType = damageType;

            this.requiredTag = requiredTag;
        }
    }

    public class BackgroundMusic
    {
        public readonly string File;
        public readonly string Type;

        public readonly Vector2 IntensityRange;
                
        public BackgroundMusic(XElement element)
        {
            this.File = element.GetAttributeString("file", "");
            this.Type = element.GetAttributeString("type", "").ToLowerInvariant();
            this.IntensityRange = element.GetAttributeVector2("intensityrange", new Vector2(0.0f, 100.0f));
        }
    }

    static class SoundPlayer
    {
        private static ILookup<string, Sound> miscSounds;
        
        //music
        public static float MusicVolume = 1.0f;
        private const float MusicLerpSpeed = 1.0f;
        private const float UpdateMusicInterval = 5.0f;

        const int MaxMusicChannels = 6;

        private readonly static Sound[] currentMusic = new Sound[MaxMusicChannels];
        private readonly static SoundChannel[] musicChannel = new SoundChannel[MaxMusicChannels];
        private readonly static BackgroundMusic[] targetMusic = new BackgroundMusic[MaxMusicChannels];
        private static List<BackgroundMusic> musicClips;

        private static float updateMusicTimer;

        //ambience
        private static List<Sound> waterAmbiences = new List<Sound>();
        private static SoundChannel[] waterAmbienceChannels = new SoundChannel[2];

        private static float ambientSoundTimer;
        private static Vector2 ambientSoundInterval = new Vector2(20.0f, 40.0f); //x = min, y = max

        //misc
        public static List<Sound> FlowSounds = new List<Sound>();
        public static List<Sound> SplashSounds = new List<Sound>();
        private static SoundChannel[] flowSoundChannels;
        private static float[] flowLeft;
        private static float[] flowRight;

        const float FlowSoundRange = 1500.0f;
        const float MaxFlowStrength = 400.0f;

        private static List<DamageSound> damageSounds;

        private static Sound startUpSound;

        public static bool Initialized;

        public static string OverrideMusicType
        {
            get;
            set;
        }

        public static float? OverrideMusicDuration;

        public static int SoundCount;
        
        public static IEnumerable<object> Init()
        {
            OverrideMusicType = null;

            List<string> soundFiles = GameMain.Config.SelectedContentPackage.GetFilesOfType(ContentType.Sounds);

            List<XElement> soundElements = new List<XElement>();
            foreach (string soundFile in soundFiles)
            {
                XDocument doc = XMLExtensions.TryLoadXml(soundFile);
                if (doc != null && doc.Root != null)
                {
                    soundElements.AddRange(doc.Root.Elements());
                }
            }
            
            SoundCount = 1 + soundElements.Count();

            var startUpSoundElement = soundElements.Find(e => e.Name.ToString().ToLowerInvariant() == "startupsound");
            if (startUpSoundElement != null)
            {
                startUpSound = GameMain.SoundManager.LoadSound(startUpSoundElement, false);
                startUpSound.Play();
            }

            yield return CoroutineStatus.Running;
                                    
            List<KeyValuePair<string, Sound>> miscSoundList = new List<KeyValuePair<string, Sound>>();
            damageSounds = new List<DamageSound>();
            musicClips = new List<BackgroundMusic>();
            
            foreach (XElement soundElement in soundElements)
            {
                yield return CoroutineStatus.Running;

                switch (soundElement.Name.ToString().ToLowerInvariant())
                {
                    case "music":
                        musicClips.Add(new BackgroundMusic(soundElement));
                        break;
                    case "splash":
                        SplashSounds.Add(GameMain.SoundManager.LoadSound(soundElement, false));
                        break;
                    case "flow":
                        FlowSounds.Add(GameMain.SoundManager.LoadSound(soundElement, false));
                        break;
                    case "waterambience":
                        waterAmbiences.Add(GameMain.SoundManager.LoadSound(soundElement, false));
                        break;
                    case "damagesound":
                        Sound damageSound = GameMain.SoundManager.LoadSound(soundElement.GetAttributeString("file", ""), false);
                        if (damageSound == null) continue;
                    
                        string damageSoundType = soundElement.GetAttributeString("damagesoundtype", "None");

                        damageSounds.Add(new DamageSound(
                            damageSound, 
                            soundElement.GetAttributeVector2("damagerange", new Vector2(0.0f, 100.0f)), 
                            damageSoundType, 
                            soundElement.GetAttributeString("requiredtag", "")));

                        break;
                    default:
                        Sound sound = GameMain.SoundManager.LoadSound(soundElement.GetAttributeString("file", ""), false);
                        if (sound != null)
                        {
                            miscSoundList.Add(new KeyValuePair<string, Sound>(soundElement.Name.ToString().ToLowerInvariant(), sound));
                        }

                        break;
                }
            }

            flowSoundChannels = new SoundChannel[FlowSounds.Count];
            flowLeft = new float[FlowSounds.Count];
            flowRight = new float[FlowSounds.Count];

            miscSounds = miscSoundList.ToLookup(kvp => kvp.Key, kvp => kvp.Value);            

            Initialized = true;

            yield return CoroutineStatus.Success;

        }
        

        public static void Update(float deltaTime)
        {
            UpdateMusic(deltaTime);

            if (startUpSound != null && !GameMain.SoundManager.IsPlaying(startUpSound))
            {
                startUpSound.Dispose();
                startUpSound = null;                
            }

            //stop water sounds if no sub is loaded
            if (Submarine.MainSub == null || Screen.Selected != GameMain.GameScreen)  
            {
                for (int i = 0; i < waterAmbienceChannels.Length; i++)
                {
                    if (waterAmbienceChannels[i] == null) continue;
                    waterAmbienceChannels[i].Dispose();
                    waterAmbienceChannels[i] = null;
                }
                for (int i = 0; i < FlowSounds.Count; i++)
                {
                    if (flowSoundChannels[i] == null) continue;
                    flowSoundChannels[i].Dispose();
                    flowSoundChannels[i] = null;
                }
                return;
            }

            float ambienceVolume = 0.8f;
            float lowpassHFGain = 1.0f;
            if (Character.Controlled != null)
            {
                AnimController animController = Character.Controlled.AnimController;
                if (animController.HeadInWater)
                {
                    ambienceVolume = 1.0f;
                    ambienceVolume += animController.Limbs[0].LinearVelocity.Length();

                    lowpassHFGain = 0.2f;
                }

                lowpassHFGain *= Character.Controlled.LowPassMultiplier;
            }

            UpdateWaterAmbience(ambienceVolume);
            UpdateWaterFlowSounds(deltaTime);
            UpdateRandomAmbience(deltaTime);
            GameMain.SoundManager.SetCategoryMuffle("default", lowpassHFGain < 0.5f);
            
        }

        private static void UpdateWaterAmbience(float ambienceVolume)
        {
            //how fast the sub is moving, scaled to 0.0 -> 1.0
            float movementSoundVolume = 0.0f;

            foreach (Submarine sub in Submarine.Loaded)
            {
                float movementFactor = (sub.Velocity == Vector2.Zero) ? 0.0f : sub.Velocity.Length() / 10.0f;
                movementFactor = MathHelper.Clamp(movementFactor, 0.0f, 1.0f);

                if (Character.Controlled == null || Character.Controlled.Submarine != sub)
                {
                    float dist = Vector2.Distance(GameMain.GameScreen.Cam.WorldViewCenter, sub.WorldPosition);
                    movementFactor = movementFactor / Math.Max(dist / 1000.0f, 1.0f);
                }

                movementSoundVolume = Math.Max(movementSoundVolume, movementFactor);
            }

            if (waterAmbiences.Count > 1)
            {
                if (waterAmbienceChannels[0] == null || !waterAmbienceChannels[0].IsPlaying)
                {
                    waterAmbienceChannels[0] = waterAmbiences[0].Play(ambienceVolume * (1.0f - movementSoundVolume),"waterambience");
                    //waterAmbiences[0].Loop(waterAmbienceIndexes[0], ambienceVolume * (1.0f - movementSoundVolume));
                    waterAmbienceChannels[0].Looping = true;
                }
                else
                {
                    waterAmbienceChannels[0].Gain = ambienceVolume * (1.0f - movementSoundVolume);
                }

                if (waterAmbienceChannels[1] == null || !waterAmbienceChannels[1].IsPlaying)
                {
                    waterAmbienceChannels[1] = waterAmbiences[1].Play(ambienceVolume * movementSoundVolume, "waterambience");
                    //waterAmbienceIndexes[1] = waterAmbiences[1].Loop(waterAmbienceIndexes[1], ambienceVolume * movementSoundVolume);
                    waterAmbienceChannels[1].Looping = true;
                }
                else
                {
                    waterAmbienceChannels[1].Gain = ambienceVolume * movementSoundVolume;
                }
            }
        }

        private static void UpdateWaterFlowSounds(float deltaTime)
        {
            float[] targetFlowLeft = new float[FlowSounds.Count];
            float[] targetFlowRight = new float[FlowSounds.Count];

            Vector2 listenerPos = new Vector2(GameMain.SoundManager.ListenerPosition.X, GameMain.SoundManager.ListenerPosition.Y);
            foreach (Gap gap in Gap.GapList)
            {
                if (gap.Open < 0.01f) continue;
                float gapFlow = Math.Abs(gap.LerpedFlowForce.X) + Math.Abs(gap.LerpedFlowForce.Y) * 2.5f;

                if (gapFlow < 10.0f) continue;

                int flowSoundIndex = (int)Math.Floor(MathHelper.Clamp(gapFlow / MaxFlowStrength, 0, FlowSounds.Count));
                flowSoundIndex = Math.Min(flowSoundIndex, FlowSounds.Count - 1);

                Vector2 diff = gap.WorldPosition - listenerPos;
                if (Math.Abs(diff.X) < FlowSoundRange && Math.Abs(diff.Y) < FlowSoundRange)
                {
                    float dist = diff.Length();
                    float distFallOff = dist / FlowSoundRange;
                    if (distFallOff >= 0.99f) continue;

                    //flow at the left side
                    if (diff.X < 0)
                    {
                        targetFlowLeft[flowSoundIndex] = 1.0f - distFallOff;
                    }
                    else
                    {
                        targetFlowRight[flowSoundIndex] = 1.0f - distFallOff;
                    }
                }
            }

            for (int i = 0; i < FlowSounds.Count; i++)
            {
                flowLeft[i] = (targetFlowLeft[i] < flowLeft[i]) ?
                    Math.Max(targetFlowLeft[i], flowLeft[i] - deltaTime) :
                    Math.Min(targetFlowLeft[i], flowLeft[i] + deltaTime);
                flowRight[i] = (targetFlowRight[i] < flowRight[i]) ?
                     Math.Max(targetFlowRight[i], flowRight[i] - deltaTime) :
                     Math.Min(targetFlowRight[i], flowRight[i] + deltaTime);

                if (flowLeft[i] < 0.05f && flowRight[i] < 0.05f)
                {
                    if (flowSoundChannels[i] != null)
                    {
                        flowSoundChannels[i].Dispose();
                        flowSoundChannels[i] = null;
                    }
                }
                else
                {
                    Vector2 soundPos = new Vector2(GameMain.SoundManager.ListenerPosition.X + (flowRight[i] - flowLeft[i]) * 100, GameMain.SoundManager.ListenerPosition.Y);
                    if (flowSoundChannels[i] == null || !flowSoundChannels[i].IsPlaying)
                    {
                        flowSoundChannels[i] = FlowSounds[i].Play(1.0f, FlowSoundRange, soundPos);
                        flowSoundChannels[i].Looping = true;
                    }
                    flowSoundChannels[i].Gain = Math.Max(flowRight[i], flowLeft[i]);
                    flowSoundChannels[i].Position = new Vector3(soundPos, 0.0f);

                }
            }
        }

        private static void UpdateRandomAmbience(float deltaTime)
        {
            if (ambientSoundTimer > 0.0f)
            {
                ambientSoundTimer -= deltaTime;
            }
            else
            {
                PlaySound(
                    "ambient",
                    Rand.Range(0.5f, 1.0f), 
                    1000.0f, 
                    new Vector2(GameMain.SoundManager.ListenerPosition.X, GameMain.SoundManager.ListenerPosition.Y) + Rand.Vector(100.0f));

                ambientSoundTimer = Rand.Range(ambientSoundInterval.X, ambientSoundInterval.Y);
            }
        }

        public static Sound GetSound(string soundTag)
        {
            var matchingSounds = miscSounds[soundTag].ToList();
            if (matchingSounds.Count == 0) return null;

            return matchingSounds[Rand.Int(matchingSounds.Count)];
        }

        public static void PlaySound(string soundTag, float volume = 1.0f)
        {
            var sound = GetSound(soundTag);            
            if (sound != null) sound.Play(volume);
        }

        public static void PlaySound(string soundTag, float volume, float range, Vector2 position)
        {
            var sound = GetSound(soundTag);
            if (sound != null) sound.Play(volume, range, position);
        }

        private static void UpdateMusic(float deltaTime)
        {
            if (musicClips == null) return;

            if (OverrideMusicType != null && OverrideMusicDuration.HasValue)
            {
                OverrideMusicDuration -= deltaTime;
                if (OverrideMusicDuration <= 0.0f)
                {
                    OverrideMusicType = null;
                    OverrideMusicDuration = null;
                }                
            }

            updateMusicTimer -= deltaTime;
            if (updateMusicTimer <= 0.0f)
            {
                //find appropriate music for the current situation
                string currentMusicType = GetCurrentMusicType();
                float currentIntensity = GameMain.GameSession?.EventManager != null ?
                    GameMain.GameSession.EventManager.CurrentIntensity * 100.0f : 0.0f;

                IEnumerable<BackgroundMusic> suitableMusic = GetSuitableMusicClips(currentMusicType, currentIntensity);

                if (suitableMusic.Count() == 0)
                {
                    targetMusic[0] = null;
                }
                //switch the music if nothing playing atm or the currently playing clip is not suitable anymore
                else if (targetMusic[0] == null || currentMusic[0] == null || !suitableMusic.Any(m => m.File == currentMusic[0].Filename))
                {
                    targetMusic[0] = suitableMusic.GetRandom();                    
                }
                                
                //get the appropriate intensity layers for current situation
                IEnumerable<BackgroundMusic> suitableIntensityMusic = GetSuitableMusicClips("intensity", currentIntensity);

                for (int i = 1; i < MaxMusicChannels; i++)
                {
                    //disable targetmusics that aren't suitable anymore
                    if (targetMusic[i] != null && !suitableIntensityMusic.Any(m => m.File == targetMusic[i].File))
                    {
                        targetMusic[i] = null;
                    }
                }
                    
                foreach (BackgroundMusic intensityMusic in suitableIntensityMusic)
                {
                    //already playing, do nothing
                    if (targetMusic.Any(m => m != null && m.File == intensityMusic.File)) continue;

                    for (int i = 1; i < MaxMusicChannels; i++)
                    {
                        if (targetMusic[i] == null)
                        {
                            targetMusic[i] = intensityMusic;
                            break;
                        }
                    }
                }                

                updateMusicTimer = UpdateMusicInterval;
            }

            for (int i = 0; i < MaxMusicChannels; i++)
            {
                //nothing should be playing on this channel
                if (targetMusic[i] == null)
                {
                    if (musicChannel[i] != null && musicChannel[i].IsPlaying)
                    {
                        //mute the channel
                        musicChannel[i].Gain = MathHelper.Lerp(musicChannel[i].Gain, 0.0f, MusicLerpSpeed * deltaTime);
                        if (musicChannel[i].Gain < 0.01f)
                        {
                            musicChannel[i].Dispose(); musicChannel[i] = null;
                            currentMusic[i].Dispose(); currentMusic[i] = null;
                        }
                    }
                }
                //something should be playing, but the channel is playing nothing or an incorrect clip
                else if (currentMusic[i] == null || targetMusic[i].File != currentMusic[i].Filename)
                {
                    //something playing -> mute it first
                    if (musicChannel[i] != null && musicChannel[i].IsPlaying)
                    {
                        musicChannel[i].Gain = MathHelper.Lerp(musicChannel[i].Gain, 0.0f, MusicLerpSpeed * deltaTime);
                        if (musicChannel[i].Gain < 0.01f)
                        {
                            musicChannel[i].Dispose(); musicChannel[i] = null;
                            currentMusic[i].Dispose(); currentMusic[i] = null;
                        }
                    }
                    //channel free now, start playing the correct clip
                    if (currentMusic[i] == null || (musicChannel[i] == null || !musicChannel[i].IsPlaying))
                    {
                        currentMusic[i] = GameMain.SoundManager.LoadSound(targetMusic[i].File, true);
                        if (musicChannel[i] != null) musicChannel[i].Dispose();
                        musicChannel[i] = currentMusic[i].Play(0.0f, "music");
                    }
                }
                else
                {
                    //playing something, lerp volume up
                    if (musicChannel[i] == null || !musicChannel[i].IsPlaying)
                    {
                        musicChannel[i].Dispose();
                        musicChannel[i] = currentMusic[i].Play(0.0f, "music");
                    }
                    musicChannel[i].Gain = MathHelper.Lerp(musicChannel[i].Gain, MusicVolume, MusicLerpSpeed * deltaTime);
                }
            } 
        }
        
        private static IEnumerable<BackgroundMusic> GetSuitableMusicClips(string musicType, float currentIntensity)
        {
            return musicClips.Where(music => 
                music != null && 
                music.Type == musicType && 
                currentIntensity >= music.IntensityRange.X &&
                currentIntensity <= music.IntensityRange.Y);
        }

        private static string GetCurrentMusicType()
        {
            if (OverrideMusicType != null) return OverrideMusicType;
            
            if (Character.Controlled != null &&
                Level.Loaded != null && Level.Loaded.Ruins != null &&
                Level.Loaded.Ruins.Any(r => r.Area.Contains(Character.Controlled.WorldPosition)))
            {
                return "ruins";
            }

            Submarine targetSubmarine = Character.Controlled?.Submarine;

            if ((targetSubmarine != null && targetSubmarine.AtDamageDepth) ||
                (Screen.Selected == GameMain.GameScreen && GameMain.GameScreen.Cam.Position.Y < SubmarineBody.DamageDepth))
            {
                return "deep";
            }

            if (targetSubmarine != null)
            {                
                float floodedArea = 0.0f;
                float totalArea = 0.0f;
                foreach (Hull hull in Hull.hullList)
                {
                    if (hull.Submarine != targetSubmarine) continue;
                    floodedArea += hull.WaterVolume;
                    totalArea += hull.Volume;
                }

                if (totalArea > 0.0f && floodedArea / totalArea > 0.25f) return "flooded";             
            }
            
            float enemyDistThreshold = 5000.0f;

            if (targetSubmarine != null)
            {
                enemyDistThreshold = Math.Max(enemyDistThreshold, Math.Max(targetSubmarine.Borders.Width, targetSubmarine.Borders.Height) * 2.0f);
            }

            foreach (Character character in Character.CharacterList)
            {
                EnemyAIController enemyAI = character.AIController as EnemyAIController;
                if (enemyAI == null || (!enemyAI.AttackHumans && !enemyAI.AttackRooms)) continue;

                if (targetSubmarine != null)
                {
                    if (Vector2.DistanceSquared(character.WorldPosition, targetSubmarine.WorldPosition) < enemyDistThreshold * enemyDistThreshold)
                    {
                        return "monster";
                    }
                }
                else if (Character.Controlled != null)
                {
                    if (Vector2.DistanceSquared(character.WorldPosition, Character.Controlled.WorldPosition) < enemyDistThreshold * enemyDistThreshold)
                    {
                        return "monster";
                    }
                }
            }

            if (GameMain.GameSession != null && Timing.TotalTime < GameMain.GameSession.RoundStartTime + 120.0)
            {
                return "start";
            }

            return "default";
        }

        public static void PlaySplashSound(Vector2 worldPosition, float strength)
        {
            int splashIndex = MathHelper.Clamp((int)(strength + Rand.Range(-2, 2)), 0, SplashSounds.Count - 1);

            SplashSounds[splashIndex].Play(1.0f, 800.0f, worldPosition);
        }

        public static void PlayDamageSound(string damageType, float damage, PhysicsBody body)
        {
            Vector2 bodyPosition = body.DrawPosition;

            PlayDamageSound(damageType, damage, bodyPosition, 800.0f);
        }

        public static void PlayDamageSound(string damageType, float damage, Vector2 position, float range = 2000.0f, IEnumerable<string> tags = null)
        {
            damage = MathHelper.Clamp(damage + Rand.Range(-10.0f, 10.0f), 0.0f, 100.0f);
            var sounds = damageSounds.FindAll(s =>
                s.damageRange == null ||
                (damage >= s.damageRange.X &&
                damage <= s.damageRange.Y) &&
                s.damageType == damageType &&
                (tags == null ? string.IsNullOrEmpty(s.requiredTag) : tags.Contains(s.requiredTag)));

            if (!sounds.Any()) return;

            int selectedSound = Rand.Int(sounds.Count);

            sounds[selectedSound].sound.Play(1.0f, range, position);
            Debug.WriteLine("playing: " + sounds[selectedSound].sound);
        }
        
    }
}

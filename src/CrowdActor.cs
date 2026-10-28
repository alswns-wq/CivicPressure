using System;
using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;

namespace CivicPressure
{
    // Visual-only skeleton: no prefab components, CitizenAI, colliders or game-buffer writes.
    internal sealed class CrowdActor
    {
        internal GameObject Root;
        private GameObject rig;
        private Animator animator;
        private Transform hand, otherHand, sign;
        private Transform fallbackArm, fallbackLeftLeg, fallbackRightLeg;
        private Material board;
        private readonly List<Material> owned = new List<Material>();
        private int walkingState = -1;
        private bool rigReady;
        private bool boardPainted, boardRiot;
        private static int cameraFrame=-1;
        private static Camera frameCamera;
        private static Camera ViewCamera
        {
            get {
                int frame=Time.frameCount;
                if(cameraFrame!=frame) { cameraFrame=frame; frameCamera=Camera.main; }
                return frameCamera;
            }
        }
        private readonly GameObject[] signText = new GameObject[2];
        private readonly int index;
        private readonly bool police;
        private Transform baton;
        private Transform combatWeapon;
        private Transform launcher;
        private Transform casualtyMark;
        private Texture2D skullTexture;
        private Material launcherAccent;
        internal void SetLiveAmmo(bool live) { if(launcherAccent!=null) launcherAccent.color=live ? new Color(.12f,.14f,.16f) : new Color(1f,.32f,.025f); }
        private Transform rightFoot, leftFoot;
        private float devNextBone,devBoneDelta;
        private bool devDetailed=true;
        private static int devViewFrame=-1;
        private static Camera devViewCamera;
        private static Plane[] devViewPlanes;
        internal static bool InView(Vector3 position)
        {
            Camera camera=ViewCamera;
            if(camera==null) return true;
            if(devViewFrame!=Time.frameCount || devViewCamera!=camera) {
                devViewFrame=Time.frameCount; devViewCamera=camera;
                devViewPlanes=GeometryUtility.CalculateFrustumPlanes(camera);
            }
            // Individual generous bounds include shields, placards and airborne attackers.
            // Never cull the entire group by its center: stragglers can be on a different street.
            return (position-camera.transform.position).sqrMagnitude<12*12 ||
                GeometryUtility.TestPlanesAABB(devViewPlanes,new Bounds(position+Vector3.up,new Vector3(16,16,16)));
        }
        private static readonly string[] slogans = { "HEAR US", "CLEAN AIR", "OUR CITY", "FAIR TAX", "WE MATTER", "FIX IT" };

        internal CrowdActor(Transform parent, CitizenInfo prefab, int number)
            : this(parent, prefab, number, false) { }

        internal CrowdActor(Transform parent, CitizenInfo prefab, int number, bool officer)
        {
            index = number; police = officer;
            Root = new GameObject("CivicPressure_Marcher_" + number);
            Root.transform.parent = parent;
            Root.SetActive(false);
            try
            {
            bool native = false;
            if (prefab != null)
            {
                try { Native(prefab); native = true; }
                catch (Exception e)
                {
                    if (rig != null) { rig.SetActive(false); UnityEngine.Object.Destroy(rig); }
                    animator = null; hand = otherHand = null;
                    Debug.LogWarning("[CivicPressure] Citizen visual fallback: " + prefab.name + ": " + e.Message);
                }
            }
            if (!native) Fallback();
            if (police) Shield(); else Placard();
            }
            catch { Dispose(); throw; }
        }

        private void Native(CitizenInfo info)
        {
            if (info.m_skinRenderer == null || info.m_skinRenderer.sharedMesh == null)
                throw new InvalidOperationException("Missing citizen mesh");
            // Preserve Unity's hidden optimized avatar/skin bindings. Root is inactive, so
            // cloned prefab behaviours cannot start before removal. Never deoptimize the source.
            rig = UnityEngine.Object.Instantiate(info.gameObject, Root.transform, false);
            rig.name = info.name + "_visual";
            rig.SetActive(false);
            foreach (MonoBehaviour behaviour in rig.GetComponentsInChildren<MonoBehaviour>(true))
                UnityEngine.Object.DestroyImmediate(behaviour);
            foreach (Collider collider in rig.GetComponentsInChildren<Collider>(true))
                UnityEngine.Object.DestroyImmediate(collider);
            foreach (Rigidbody body in rig.GetComponentsInChildren<Rigidbody>(true))
                UnityEngine.Object.DestroyImmediate(body);
            rig.transform.localPosition = Vector3.zero;
            rig.transform.localRotation = Quaternion.identity;
            animator = rig.GetComponent<Animator>();
            if (animator == null || animator.runtimeAnimatorController == null)
                throw new InvalidOperationException("Missing cloned animator");
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.enabled = false;
            bool found = false;
            foreach (Renderer renderer in rig.GetComponentsInChildren<Renderer>(true))
            {
                SkinnedMeshRenderer skin = renderer as SkinnedMeshRenderer;
                if (skin == null) { renderer.enabled = false; continue; }
                found = true; skin.enabled = true; skin.updateWhenOffscreen = true;
                Material[] materials = skin.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    if (materials[i] == null) throw new InvalidOperationException("Missing citizen material");
                }
                // Native skin materials are never edited by this visual clone; share them for batching.
                skin.localBounds = new Bounds(Vector3.up, new Vector3(4, 6, 4));
            }
            if (!found) throw new InvalidOperationException("No cloned skin renderer");
            rig.SetActive(true); // Parent stays inactive until the actor is shown.
        }

        private void InitializeRig()
        {
            animator.Rebind();
            animator.Update(0);
            AnimatorUtility.DeoptimizeTransformHierarchy(rig);
            animator.Rebind();
            animator.Update(0);
            foreach (Transform bone in rig.GetComponentsInChildren<Transform>(true))
            {
                string name = bone.name.ToLowerInvariant().Replace(" ", "").Replace("_", "");
                if (hand == null && (name.Contains("righthand") || name.Contains("rhand") || name == "hand.r")) hand = bone;
                if (otherHand == null && (name.Contains("lefthand") || name.Contains("lhand") || name == "hand.l")) otherHand = bone;
                if (rightFoot == null && (name.Contains("rightfoot") || name.Contains("rfoot") || name == "foot.r")) rightFoot = bone;
                if (leftFoot == null && (name.Contains("leftfoot") || name.Contains("lfoot") || name == "foot.l")) leftFoot = bone;
            }
            rigReady = true;
            Debug.Log("[CivicPressure] Crowd rig initialized: " + rig.name + ", transforms=" + rig.GetComponentsInChildren<Transform>(true).Length + ", human=" + animator.isHuman);
        }
        private Material Paint(Color color)
        {
            Shader shader = Shader.Find("Diffuse");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) throw new InvalidOperationException("No crowd prop shader");
            Material material = new Material(shader); material.color = color; owned.Add(material); return material;
        }
        private static Transform Part(Transform parent, string name, Vector3 position, Vector3 scale, Material material, PrimitiveType shape)
        {
            GameObject obj = GameObject.CreatePrimitive(shape); obj.name = name;
            obj.transform.parent = parent; obj.transform.localPosition = position;
            obj.transform.localRotation = Quaternion.identity; obj.transform.localScale = scale;
            Collider collider = obj.GetComponent<Collider>();
            if (collider != null) { collider.enabled = false; UnityEngine.Object.Destroy(collider); }
            obj.GetComponent<Renderer>().sharedMaterial = material; return obj.transform;
        }
        private void Fallback()
        {
            Material clothes = Paint(police ? new Color(.08f, .15f, .26f) : Color.HSVToRGB((index * .137f) % 1, .5f, .65f));
            Material skin = Paint(new Color(.72f, .52f, .38f));
            Part(Root.transform, "Torso", new Vector3(0, 1.05f, 0), new Vector3(.46f, .64f, .25f), clothes, PrimitiveType.Cube);
            Part(Root.transform, "Head", new Vector3(0, 1.55f, 0), Vector3.one * .29f, skin, PrimitiveType.Sphere);
            if (police) Part(Root.transform, "PoliceCap", new Vector3(0, 1.72f, .015f), new Vector3(.36f, .07f, .38f), clothes, PrimitiveType.Cylinder);
            fallbackLeftLeg = new GameObject("LeftLeg").transform; fallbackLeftLeg.parent = Root.transform; fallbackLeftLeg.localPosition = new Vector3(-.13f, .77f, 0);
            fallbackRightLeg = new GameObject("RightLeg").transform; fallbackRightLeg.parent = Root.transform; fallbackRightLeg.localPosition = new Vector3(.13f, .77f, 0);
            Part(fallbackLeftLeg, "Leg", new Vector3(0, -.36f, 0), new Vector3(.17f, .72f, .18f), clothes, PrimitiveType.Cube);
            Part(fallbackRightLeg, "Leg", new Vector3(0, -.36f, 0), new Vector3(.17f, .72f, .18f), clothes, PrimitiveType.Cube);
            fallbackArm = Part(Root.transform, "FreeArm", new Vector3(-.3f, 1.02f, .06f), new Vector3(.14f, .6f, .14f), clothes, PrimitiveType.Cube);
            Part(Root.transform, "SignArm", new Vector3(.3f, 1.12f, .17f), new Vector3(.15f, .45f, .15f), clothes, PrimitiveType.Cube);
        }
        private void Shield()
        {
            sign = new GameObject("RiotShield").transform; sign.parent = Root.transform;
            board = Paint(new Color(.12f, .19f, .27f));
            Material rim = Paint(new Color(.45f, .5f, .55f));
            Part(sign, "ShieldRim", Vector3.zero, new Vector3(.70f, 1.04f, .08f), rim, PrimitiveType.Cube);
            Part(sign, "ShieldFace", new Vector3(0, 0, .05f), new Vector3(.63f, .96f, .04f), board, PrimitiveType.Cube);
            Font font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (font != null)
            {
                GameObject obj = new GameObject("PoliceLabel"); signText[1] = obj; obj.transform.parent = sign;
                obj.transform.localPosition = new Vector3(0, .05f, .079f); obj.transform.localRotation = Quaternion.Euler(0, 180, 0);
                TextMesh text = obj.AddComponent<TextMesh>(); text.font = font; text.fontSize = 48; text.characterSize = .045f;
                text.anchor = TextAnchor.MiddleCenter; text.text = "POLICE"; text.color = Color.white;
                obj.GetComponent<Renderer>().sharedMaterial = font.material;
            }
        }
        private void Placard()
        {
            sign = new GameObject("HandheldPlacard").transform; sign.parent = Root.transform;
            Material wood = Paint(new Color(.35f, .22f, .10f)); board = Paint(new Color(.95f, .93f, .80f));
            Part(sign, "Pole", new Vector3(0, .42f, 0), new Vector3(.04f, 1.15f, .04f), wood, PrimitiveType.Cube);
            Part(sign, "Board", new Vector3(0, .93f, 0), new Vector3(.95f, .57f, .055f), board, PrimitiveType.Cube);
            Font font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (font == null) return;
            for (int side = 0; side < 2; side++)
            {
                GameObject obj = new GameObject("Slogan"); obj.transform.parent = sign;
                signText[side] = obj;
                obj.transform.localPosition = new Vector3(0, .93f, side == 0 ? -.033f : .033f);
                obj.transform.localRotation = Quaternion.Euler(0, side == 0 ? 0 : 180, 0);
                TextMesh text = obj.AddComponent<TextMesh>(); text.font = font; text.fontSize = 48; text.characterSize = .06f;
                text.anchor = TextAnchor.MiddleCenter; text.alignment = TextAlignment.Center;
                text.text = slogans[index % slogans.Length]; text.color = new Color(.12f, .1f, .08f);
                obj.GetComponent<Renderer>().sharedMaterial = font.material;
            }
        }

        internal void Show(bool visible) { if(Root!=null && Root.activeSelf!=visible)Root.SetActive(visible); }
        internal void HidePlacard() { if (sign != null) sign.gameObject.SetActive(false); }
        internal void SetPlacardVisible(bool visible) { if (sign != null) sign.gameObject.SetActive(visible); if(visible) CasualtyMark(0,Vector3.zero); }
        internal void HideBaton() { if (baton != null) baton.gameObject.SetActive(false); }
        internal void CasualtyMark(int kind,Vector3 position)
        {
            Camera camera=ViewCamera;
            bool visible=kind==2 && camera!=null && (camera.transform.position-position).sqrMagnitude<=100*100;
            if(casualtyMark==null && visible) {
                Shader shader=Shader.Find("Unlit/Transparent"); if(shader==null) return;
                // Small font-independent skull, created only for a visible fatality.
                string[] rows={
                    "................",".....######.....","...##########...","..############..",
                    "..############..",".##############.",".##...####...##.",".##...####...##.",
                    ".##############.","..#####..#####..","...####..####...","....########....",
                    "....#.#..#.#....","....########....",".....######.....","................"};
                skullTexture=new Texture2D(16,16,TextureFormat.ARGB32,false);
                skullTexture.filterMode=FilterMode.Point; skullTexture.wrapMode=TextureWrapMode.Clamp;
                Color[] pixels=new Color[256];
                for(int y=0;y<16;y++) for(int x=0;x<16;x++)
                    pixels[(15-y)*16+x]=rows[y][x]=='#' ? Color.white : Color.clear;
                skullTexture.SetPixels(pixels); skullTexture.Apply(false,true);
                Material material=new Material(shader); material.mainTexture=skullTexture; owned.Add(material);
                casualtyMark=Part(Root.transform,"FatalitySkull",Vector3.zero,Vector3.one*.65f,material,PrimitiveType.Quad);
            }
            if(casualtyMark==null) return;
            if(casualtyMark.gameObject.activeSelf!=visible) casualtyMark.gameObject.SetActive(visible);
            if(!visible) return;
            casualtyMark.position=position+Vector3.up*1.25f;
            casualtyMark.rotation=camera.transform.rotation;
        }
        internal void CasualtyPose(Quaternion facing,bool dead,float fall,float time)
        {
            if(fall<=0) return;
            Quaternion body=facing*Quaternion.Euler((dead ? -90 : 15)*fall,0,dead ? 0 : 85*fall);
            Root.transform.rotation=body;
            if(rig!=null) rig.transform.localRotation=Quaternion.Euler(dead ? 0 : 20*fall,0,0);
            if(!dead) Root.transform.position+=Vector3.up*(.008f*Mathf.Sin(time*2+index)*fall);
            Vector3 arms=body*(dead ? new Vector3(.12f,-1,.05f) : new Vector3(.05f,.25f,.7f));
            if(hand!=null && hand.parent!=null) Aim(hand.parent,hand,arms);
            if(otherHand!=null && otherHand.parent!=null) Aim(otherHand.parent,otherHand,arms);
            if(rightFoot!=null && rightFoot.parent!=null) Aim(rightFoot.parent,rightFoot,body*new Vector3(0,-1,dead ? 0 : -.65f*fall));
            if(leftFoot!=null && leftFoot.parent!=null) Aim(leftFoot.parent,leftFoot,body*new Vector3(0,-1,dead ? 0 : -.55f*fall));
            if(fallbackArm!=null) fallbackArm.localRotation=Quaternion.Euler(dead ? 0 : -90*fall,0,0);
            if(fallbackLeftLeg!=null) fallbackLeftLeg.localRotation=Quaternion.Euler(dead ? 0 : 55*fall,0,0);
            if(fallbackRightLeg!=null) fallbackRightLeg.localRotation=Quaternion.Euler(dead ? 0 : 65*fall,0,0);
        }
        internal void CustodyPose(Quaternion facing,float boarding)
        {
            Vector3 arm=facing*new Vector3(.05f,-.7f,-.35f);
            if(hand!=null && hand.parent!=null) Aim(hand.parent,hand,arm);
            if(otherHand!=null && otherHand.parent!=null) Aim(otherHand.parent,otherHand,arm);
            if(rig!=null) rig.transform.localRotation=Quaternion.Euler(10+boarding*35,0,0);
            if(fallbackArm!=null) fallbackArm.localRotation=Quaternion.Euler(25,0,0);
            Root.transform.position-=Vector3.up*(boarding*.28f);
        }
        internal void CombatBody(Quaternion facing,DevCombatMotion motion)
        {
            Root.transform.rotation=facing*Quaternion.Euler(85*motion.Fall,0,motion.Roll);
            Root.transform.position+=Vector3.up*(motion.Fall*.08f-motion.Kneel);
            if(rig!=null) rig.transform.localRotation=Quaternion.Euler(motion.Lean,motion.Twist,0);
            else Root.transform.rotation*=Quaternion.Euler(motion.Lean,motion.Twist,0);
            if(motion.Brace>.05f) {
                Quaternion body=Root.transform.rotation;
                Vector3 arm=body*new Vector3(.1f,.45f,.7f);
                if(hand!=null && hand.parent!=null) Aim(hand.parent,hand,arm);
                if(otherHand!=null && otherHand.parent!=null) Aim(otherHand.parent,otherHand,arm);
                if(fallbackArm!=null) fallbackArm.localRotation=Quaternion.Euler(-100*motion.Brace,0,0);
            }
            if(police && motion.Fall==0) {
                // Keep the shield between the officer and crowd while bracing, without moving the combat anchor.
                sign.position=Root.transform.position+facing*new Vector3(-.22f,.98f-motion.Kneel,.45f+motion.Brace*.12f);
                sign.rotation=facing*Quaternion.Euler(-5-motion.Brace*16,0,-motion.Roll*.3f);
            }
            if(motion.Fall>0 || motion.Kneel>0) {
                Quaternion body=Root.transform.rotation;
                if(rightFoot!=null && rightFoot.parent!=null) Aim(rightFoot.parent,rightFoot,body*new Vector3(0,-1,.35f*motion.Fall+motion.Kneel));
                if(leftFoot!=null && leftFoot.parent!=null) Aim(leftFoot.parent,leftFoot,body*new Vector3(0,-1,-.2f*motion.Fall-motion.Kneel));
                if(fallbackLeftLeg!=null) fallbackLeftLeg.localRotation=Quaternion.Euler(-35*motion.Fall-90*motion.Kneel,0,0);
                if(fallbackRightLeg!=null) fallbackRightLeg.localRotation=Quaternion.Euler(25*motion.Fall+90*motion.Kneel,0,0);
            }
        }
        internal void PoliceGuard(Quaternion facing,DevCombatMotion motion,float time,bool moving)
        {
            float breath=Mathf.Sin(time*1.7f+index*1.9f);
            float brace=motion.Brace;
            if(rig!=null) rig.transform.localRotation=Quaternion.Euler(motion.Lean+5+brace*9+breath*.6f,motion.Twist-8,0);
            // Plant a staggered stance only while standing; preserve the native walk cycle in motion.
            if(!moving && devDetailed) {
                Vector3 feet=Root.transform.position;
                Reach(leftFoot,feet+facing*new Vector3(-.21f,.02f,.17f),facing*Vector3.forward);
                Reach(rightFoot,feet+facing*new Vector3(.21f,.02f,-.24f),facing*Vector3.forward);
                if(fallbackLeftLeg!=null) fallbackLeftLeg.localRotation=Quaternion.Euler(-12-brace*12,0,0);
                if(fallbackRightLeg!=null) fallbackRightLeg.localRotation=Quaternion.Euler(15+brace*12,0,0);
            }
            sign.position=Root.transform.position+facing*new Vector3(-.24f,1.02f+breath*.01f,.46f+brace*.1f);
            sign.rotation=facing*Quaternion.Euler(-8-brace*18,5,-motion.Roll*.3f);
            Reach(otherHand,sign.position-facing*new Vector3(0,0,.12f),facing*Vector3.left);
        }
        internal void MedicalPose(Quaternion facing,float time)
        {
            CombatBody(facing,new DevCombatMotion { Kneel=.45f,Lean=30 });
            Vector3 point=Root.transform.position+facing*new Vector3(0,.35f,.6f);
            float check=Mathf.Sin(time*3)*.04f;
            Reach(hand,point+facing*new Vector3(.15f,check,0),facing*Vector3.right);
            Reach(otherHand,point+facing*new Vector3(-.15f,-check,.1f),facing*Vector3.left);
        }
        private void EnsureLauncher()
        {
            if(launcher==null) {
                launcher=new GameObject("RubberLauncher").transform; launcher.parent=Root.transform;
                Material black=Paint(new Color(.055f,.065f,.075f)), orange=Paint(new Color(1f,.32f,.025f));
                launcherAccent=orange;
                Part(launcher,"Receiver",new Vector3(0,.05f,.10f),new Vector3(.17f,.18f,.28f),black,PrimitiveType.Cube);
                Transform barrel=Part(launcher,"OrangeBarrel",new Vector3(0,.07f,.34f),new Vector3(.21f,.15f,.21f),orange,PrimitiveType.Cylinder);
                barrel.localRotation=Quaternion.Euler(90,0,0);
                Transform muzzle=Part(launcher,"Muzzle",new Vector3(0,.07f,.492f),new Vector3(.14f,.012f,.14f),black,PrimitiveType.Cylinder);
                muzzle.localRotation=Quaternion.Euler(90,0,0);
                Part(launcher,"Grip",new Vector3(0,-.10f,0),new Vector3(.09f,.22f,.11f),orange,PrimitiveType.Cube);
                Part(launcher,"Stock",new Vector3(0,.01f,-.19f),new Vector3(.10f,.17f,.29f),black,PrimitiveType.Cube);
            }
        }
        internal void CombatWeapon(Quaternion facing, bool gun, bool plank, bool stone, float age)
        {
            bool visible=gun || plank || stone;
            if(gun) EnsureLauncher();
            if(launcher!=null) launcher.gameObject.SetActive(gun);
            if(combatWeapon==null && (plank || stone))
                combatWeapon=Part(Root.transform,"CombatProp",Vector3.zero,Vector3.one,Paint(new Color(.25f,.2f,.15f)),PrimitiveType.Cube);
            if(combatWeapon!=null) combatWeapon.gameObject.SetActive(plank || stone);
            if(!visible) return;
            HideBaton();
            float swing=DevVisualMotion.Strike(age,1);
            float angle=stone ? -110+155*Mathf.Clamp01(age/.55f) : -30-95*swing;
            float recoil=gun ? Mathf.Max(0,1-age/.18f)*.16f : 0;
            Vector3 arm=facing*(gun ? new Vector3(.1f,.05f+recoil,1) : Quaternion.Euler(angle,0,plank ? -20*swing : 0)*Vector3.forward);
            if(hand!=null && hand.parent!=null) Aim(hand.parent,hand,arm);
            if(gun && otherHand!=null && otherHand.parent!=null) Aim(otherHand.parent,otherHand,arm);
            if(gun && police) {
                Vector3 shoulder=Root.transform.position+facing*new Vector3(.23f,1.35f,.32f-recoil);
                Reach(hand,shoulder,facing*Vector3.right);
                Reach(otherHand,shoulder+facing*new Vector3(0,.01f,.29f),facing*Vector3.left);
                sign.position=Root.transform.position+facing*new Vector3(-.43f,.92f,.25f);
                sign.rotation=facing*Quaternion.Euler(-12,-30,-12);
            }
            if(gun) {
                launcher.position=Root.transform.position+facing*new Vector3(.23f,1.38f,.32f-recoil);
                launcher.rotation=facing*Quaternion.Euler(-recoil*30,0,0);
                return;
            }
            combatWeapon.localScale=plank ? new Vector3(.12f,.8f,.06f) : Vector3.one*.14f;
            combatWeapon.position=(hand!=null ? hand.position : Root.transform.position+facing*new Vector3(.3f,1.3f,.4f))+arm*.15f;
            combatWeapon.rotation=facing*Quaternion.Euler(gun ? -recoil*30 : angle,0,plank ? -20*swing : 0);
            // The existing projectile takes over after release; do not keep a second stone in the hand.
            if(stone && age>=.18f) combatWeapon.gameObject.SetActive(false);
        }
        internal void GroundWeapon(Quaternion facing,Vector3 position,bool visible,bool live)
        {
            if(visible) EnsureLauncher();
            if(launcher!=null) launcher.gameObject.SetActive(visible);
            if(!visible) return;
            SetLiveAmmo(live);
            launcher.position=position+Vector3.up*.12f+facing*new Vector3(.45f,0,.3f);
            launcher.rotation=facing*Quaternion.Euler(0,35,90);
        }
        internal void PoliceAction(Quaternion facing, float strike, bool throwing, float progress)
        {
            if (baton == null)
                baton = Part(Root.transform, "RiotBaton", Vector3.zero, new Vector3(.06f, .32f, .06f), Paint(new Color(.12f, .12f, .13f)), PrimitiveType.Cylinder);
            baton.gameObject.SetActive(!throwing);
            float angle = throwing ? DevVisualMotion.ThrowAngle(progress) : 35 - strike * 155;
            Vector3 arm = facing * (Quaternion.Euler(angle, 0, 0) * Vector3.forward);
            if (hand != null && hand.parent != null) Aim(hand.parent, hand, arm);
            if (fallbackArm != null) fallbackArm.localRotation = Quaternion.Euler(angle, 0, 0);
            baton.position = (hand != null ? hand.position : Root.transform.position + facing * new Vector3(.35f, 1.3f, .4f)) + arm * .22f;
            baton.rotation = facing * Quaternion.Euler(angle + 90, 0, -10);
            sign.position = Root.transform.position + facing * new Vector3(-.25f, 1.02f, .46f + Math.Max(0,strike) * .16f);
            sign.rotation = facing * Quaternion.Euler(-8 - Math.Max(0,strike) * 12, 5, 0);
            Reach(otherHand,sign.position-facing*new Vector3(0,0,.12f),facing*Vector3.left);
        }
        internal void Cower(Quaternion facing, float intensity)
        {
            if (rig != null) rig.transform.localRotation = Quaternion.Euler(15 * intensity, 0, 0);
            Vector3 arm = facing * new Vector3(.1f, .65f, .3f);
            if (hand != null && hand.parent != null) Aim(hand.parent, hand, arm);
            if (otherHand != null && otherHand.parent != null) Aim(otherHand.parent, otherHand, arm);
            if (fallbackArm != null) fallbackArm.localRotation = Quaternion.Euler(-130 * intensity, 0, 0);
        }
        internal void GripVehicle(Quaternion facing, float time, Vector3 grip)
        {
            float effort=DevVisualMotion.GripPulse(index,time);
            float lean = DevVehicleAssaultRules.GripLean(time)+effort*9, crouch = DevVehicleAssaultRules.GripCrouch(time)+Math.Abs(effort)*.07f;
            Vector3 feet = Root.transform.position;
            Root.transform.position -= Vector3.up * crouch;
            if (rig != null) rig.transform.localRotation = Quaternion.Euler(lean, effort*6, -effort*3);
            Vector3 side = facing * Vector3.right;
            Reach(hand, grip + side * .24f, facing * Vector3.down);
            Reach(otherHand, grip - side * .24f, facing * Vector3.down);
            Reach(rightFoot, feet + facing * new Vector3(.19f, .03f, .27f), facing * Vector3.forward);
            Reach(leftFoot, feet + facing * new Vector3(-.19f, .03f, -.3f), facing * Vector3.forward);
            if (fallbackArm != null) fallbackArm.localRotation = Quaternion.Euler(-80 - lean, 0, 0);
            if (fallbackLeftLeg != null) fallbackLeftLeg.localRotation = Quaternion.Euler(15 + crouch * 60, 0, 0);
            if (fallbackRightLeg != null) fallbackRightLeg.localRotation = Quaternion.Euler(-15 - crouch * 60, 0, 0);
        }
        internal void ClimbVehicle(Quaternion facing, float reach, float lift, Vector3 grip, Vector3 ground)
        {
            Vector3 feet = Root.transform.position;
            float bend = (float)Math.Sin(lift * Math.PI);
            Root.transform.position -= Vector3.up * (.12f * lift + .12f * bend);
            if (rig != null) rig.transform.localRotation = Quaternion.Euler(10 + 15 * lift + 18 * bend, 0, 0);
            Vector3 side = facing * Vector3.right;
            if (hand != null) Reach(hand, Vector3.Lerp(hand.position, grip + side * .24f, reach), facing * Vector3.down);
            if (otherHand != null) Reach(otherHand, Vector3.Lerp(otherHand.position, grip - side * .24f, reach), facing * Vector3.down);
            Vector3 right = Vector3.Lerp(ground, feet, DevVehicleAssaultRules.Ease(lift, .1f, .65f));
            Vector3 left = Vector3.Lerp(ground, feet, DevVehicleAssaultRules.Ease(lift, .35f, .65f));
            Reach(rightFoot, right + facing * new Vector3(.15f, .03f + bend * .22f, .2f), facing * Vector3.forward);
            Reach(leftFoot, left + facing * new Vector3(-.15f, .03f, -.2f), facing * Vector3.forward);
            if (fallbackArm != null) fallbackArm.localRotation = Quaternion.Euler(-90 * reach, 0, 0);
            if (fallbackRightLeg != null) fallbackRightLeg.localRotation = Quaternion.Euler(55 * bend, 0, 0);
            if (fallbackLeftLeg != null) fallbackLeftLeg.localRotation = Quaternion.Euler(-25 * bend, 0, 0);
        }
        internal void VehicleFall(Quaternion facing, float time, int actor)
        {
            if (time >= 16) return;
            float flight = DevVehicleAssaultRules.ThrowProgress(time, actor);
            float recovery = DevVehicleAssaultRules.Recovery(time, actor);
            if (rig != null) rig.transform.localRotation = Quaternion.identity;
            Vector3 origin = Root.transform.position;
            float reach = (1 - recovery) * (.6f + .25f * (float)Math.Sin(flight * Math.PI));
            Quaternion body = Root.transform.rotation;
            Reach(hand, origin + body * new Vector3(reach, 1.15f, .4f), body * Vector3.up);
            Reach(otherHand, origin + body * new Vector3(-reach, 1.15f, .4f), body * Vector3.up);
            if (rightFoot != null && rightFoot.parent != null) Aim(rightFoot.parent, rightFoot, Root.transform.rotation * new Vector3(0, -.7f, .4f * (1 - recovery)));
            if (leftFoot != null && leftFoot.parent != null) Aim(leftFoot.parent, leftFoot, Root.transform.rotation * new Vector3(0, -.7f, .6f * (1 - recovery)));
            if (recovery > 0)
            {
                float kneel = (float)Math.Sin(recovery * Math.PI);
                Root.transform.position -= Vector3.up * kneel * .18f;
                if (rightFoot != null) Reach(rightFoot, Vector3.Lerp(rightFoot.position, origin + facing * new Vector3(.2f, .03f, .3f), recovery), facing * Vector3.forward);
                if (leftFoot != null) Reach(leftFoot, Vector3.Lerp(leftFoot.position, origin + facing * new Vector3(-.2f, .03f, -.3f), recovery), facing * Vector3.forward);
                if (fallbackLeftLeg != null) fallbackLeftLeg.localRotation = Quaternion.Euler(-55 * kneel, 0, 0);
                if (fallbackRightLeg != null) fallbackRightLeg.localRotation = Quaternion.Euler(55 * kneel, 0, 0);
            }
            if (fallbackArm != null) fallbackArm.localRotation = Quaternion.Euler(-100 * (1 - recovery), 0, 0);
        }
        private static void Reach(Transform end, Vector3 target, Vector3 bend)
        {
            if (end == null || end.parent == null || end.parent.parent == null) return;
            Transform elbow = end.parent, upper = elbow.parent;
            Vector3 start = upper.position, toTarget = target - start;
            float first = (elbow.position - start).magnitude, second = (end.position - elbow.position).magnitude;
            if (first < .01f || second < .01f || toTarget.sqrMagnitude < .0001f) return;
            float distance = Mathf.Clamp(toTarget.magnitude, Math.Abs(first - second) + .001f, first + second - .001f);
            Vector3 forward = toTarget.normalized;
            Vector3 pole = bend - forward * Vector3.Dot(bend, forward);
            if (pole.sqrMagnitude < .0001f) pole = Vector3.Cross(forward, Vector3.right);
            if (pole.sqrMagnitude < .0001f) pole = Vector3.Cross(forward, Vector3.up);
            float along = (distance * distance + first * first - second * second) / (2 * distance);
            Vector3 joint = start + forward * along + pole.normalized * (float)Math.Sqrt(Math.Max(0, first * first - along * along));
            upper.rotation = Quaternion.FromToRotation(elbow.position - start, joint - start) * upper.rotation;
            elbow.rotation = Quaternion.FromToRotation(end.position - elbow.position, start + forward * distance - elbow.position) * elbow.rotation;
        }
        internal void Pose(Vector3 position, Quaternion facing, bool riot, bool moving, bool suppressed, float time, float delta)
        {
            Root.transform.position = position; Root.transform.rotation = facing;
            float wave = Mathf.Sin(time * (riot ? 7 : 3) + index * 1.7f);
            float cadence=1, lift=0;
            float gesture=0, bodyYaw=0, bodyRoll=0;
            cadence=DevProtestRules.Cadence(index);
            gesture=DevVisualMotion.Gesture(index,time);
            if(!riot && !police) { lift=DevProtestRules.Lift(index,time); wave=Mathf.Sin(time*3*cadence+index*1.7f); }
            bodyYaw=Mathf.Sin(time*.7f+index*2.4f)*(riot ? 6 : 3);
            bodyRoll=moving ? wave*(police ? 1 : riot ? 3 : 1.5f) : wave*.6f;
            if (animator != null && !rigReady)
            {
                try { InitializeRig(); }
                catch (Exception error)
                {
                    rig.SetActive(false); UnityEngine.Object.Destroy(rig);
                    animator = null; hand = otherHand = null;
                    Fallback();
                    Debug.LogWarning("[CivicPressure] Rig initialization fallback: " + error.Message);
                }
            }
            if (animator != null)
            {
                rig.transform.localRotation = Quaternion.Euler(riot && !suppressed ? 5 + wave * 4 : 0, 0, 0);
                rig.transform.localRotation=Quaternion.Euler(police ? 3 : riot && !suppressed ? 5+gesture*9 : lift*2,bodyYaw,bodyRoll);
                CitizenManager manager = Singleton<CitizenManager>.instance;
                int mode = moving ? 1 : 0;
                // Root positions and action poses still follow every frame. Only the expensive
                // native skeleton evaluation is budgeted; retain elapsed animation time.
                Camera boneCamera=ViewCamera;
                float boneDistance=boneCamera==null ? 0 : (boneCamera.transform.position-position).sqrMagnitude;
                devDetailed=boneDistance<=100*100;
                float boneInterval=DevVisualMotion.BoneInterval(boneDistance), boneNow=Time.realtimeSinceStartup;
                devBoneDelta+=delta;
                bool evaluate=mode!=walkingState || (devBoneDelta>0 && boneNow>=devNextBone);
                if(evaluate) {
                animator.SetInteger(manager.ID_State, (int)CitizenInfo.AnimationState.Default);
                animator.SetFloat(manager.ID_Speed, moving ? (riot ? 3.8f*cadence : 1.6f*cadence) : 0);
                if (mode != walkingState)
                {
                    int state = moving ? manager.ID_walk_0 : manager.ID_idle;
                    if (animator.HasState(0, state)) animator.Play(state, 0, (index * .137f) % 1);
                    walkingState = mode;
                }
                animator.Update(devBoneDelta * (moving ? (riot ? 1.9f*cadence : 1.2f*cadence) : (riot && !suppressed ? 1.6f : 1)));
                devBoneDelta=0;
                devNextBone=devNextBone==0 ? boneNow+boneInterval*(1+(index%17)/17f) : DevVisualMotion.NextBone(boneNow,devNextBone,boneInterval);
                }
                if (animator.isHuman)
                {
                    if (hand == null) hand = animator.GetBoneTransform(HumanBodyBones.RightHand);
                    if (otherHand == null) otherHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
                    if (rightFoot == null) rightFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);
                    if (leftFoot == null) leftFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
                }
                if (hand != null && hand.parent != null)
                    Aim(hand.parent, hand, facing * (police ? new Vector3(-.1f, .15f, .9f) : new Vector3(.15f, .9f+lift*.25f, .3f)));
                if (otherHand != null && otherHand.parent != null && riot && !suppressed)
                    Aim(otherHand.parent, otherHand, facing * new Vector3(-.2f, .35f + .65f * wave, .8f));
                if(!police && devDetailed) {
                    // Rest the sign between chants; each actor raises it on their own cycle.
                    if(hand!=null) Reach(hand,position+facing*new Vector3(.32f,1.12f+gesture*.5f,.28f),facing*Vector3.right);
                    if(otherHand!=null && !suppressed && gesture>.02f) {
                        Vector3 call=index%3==0 ? new Vector3(-.22f,1.5f,.3f) : new Vector3(-.35f,1.15f+gesture*.5f,.4f);
                        Reach(otherHand,Vector3.Lerp(otherHand.position,position+facing*call,gesture),facing*Vector3.left);
                    }
                }
            }
            else
            {
                fallbackLeftLeg.localRotation = Quaternion.Euler(moving ? wave * 25 : 0, 0, 0);
                fallbackRightLeg.localRotation = Quaternion.Euler(moving ? -wave * 25 : 0, 0, 0);
                fallbackArm.localRotation = Quaternion.Euler(riot && !suppressed ? -65 + wave * 55 : wave * 8, 0, 0);
                fallbackArm.localRotation=Quaternion.Euler(!police && !suppressed ? -gesture*120+wave*8 : wave*8,0,0);
            }
            if (police)
            {
                sign.position = position + facing * new Vector3(.07f, .98f, .43f);
                sign.rotation = facing * Quaternion.Euler(-5, 0, 0);
                Camera policeCamera = ViewCamera;
                if (signText[1] != null && policeCamera != null) signText[1].SetActive(Vector3.Dot(policeCamera.transform.position - sign.position, sign.forward) > 0);
                return;
            }
            sign.position = hand != null ? hand.position : position + facing * new Vector3(.35f, 1.25f+lift*.2f, .2f);
            sign.rotation = facing * Quaternion.Euler(riot && !suppressed ? wave * 22 : wave * (3+lift*5), 0, riot && !suppressed ? wave * 14 : wave * (3+lift*4));
            if(!boardPainted || boardRiot!=riot) {
                board.color = riot ? new Color(.96f, .55f, .3f) : new Color(.95f, .93f, .80f);
                boardPainted=true; boardRiot=riot;
            }
            Camera camera = ViewCamera;
            if (camera != null)
            {
                bool front = Vector3.Dot(camera.transform.position - sign.position, sign.forward) < 0;
                if (signText[0] != null) signText[0].SetActive(front);
                if (signText[1] != null) signText[1].SetActive(!front);
            }
        }
        private static void Aim(Transform joint, Transform end, Vector3 direction)
        {
            Vector3 bone = end.position - joint.position;
            if (bone.sqrMagnitude > .00001f) joint.rotation = Quaternion.FromToRotation(bone, direction) * joint.rotation;
        }
        internal void Dispose()
        {
            if(skullTexture!=null) UnityEngine.Object.Destroy(skullTexture);
            if (Root != null) UnityEngine.Object.Destroy(Root);
            foreach (Material material in owned) if (material != null) UnityEngine.Object.Destroy(material);
        }
    }
}

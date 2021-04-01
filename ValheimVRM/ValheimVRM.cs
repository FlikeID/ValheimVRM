﻿using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UniGLTF;
using UnityEngine;
using VRM;

[HarmonyPatch(typeof(Shader))]
[HarmonyPatch(nameof(Shader.Find))]
static class ShaderPatch
{
	static bool Prefix(ref Shader __result, string name)
	{
		if (VRMShaders.Shaders.TryGetValue(name, out var shader))
		{
			__result = shader;
			return false;
		}

		return true;
	}
}

public static class VRMShaders
{
	public static Dictionary<string, Shader> Shaders { get; } = new Dictionary<string, Shader>();

	public static void Initialize()
	{
		var bundlePath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), @"ValheimVRM.shaders");
		if (File.Exists(bundlePath))
		{
			var assetBundle = AssetBundle.LoadFromFile(bundlePath);
			var assets = assetBundle.LoadAllAssets<Shader>();
			foreach (var asset in assets)
			{
				UnityEngine.Debug.Log("Add Shader: " + asset.name);
				Shaders.Add(asset.name, asset);
			}
		}
	}
}

[HarmonyPatch(typeof(Humanoid), "SetupVisEquipment")]
static class Patch_Humanoid_SetupVisEquipment
{
	[HarmonyPostfix]
	static void Postfix(Humanoid __instance, VisEquipment visEq, bool isRagdoll)
	{
		if (!__instance.IsPlayer()) return;

		visEq.SetHairItem("");
		visEq.SetBeardItem("");
		visEq.SetHelmetItem("");
		visEq.SetChestItem("");
		visEq.SetLegItem("");
		visEq.SetShoulderItem("", 0);
	}
}

[HarmonyPatch(typeof(Player), "Awake")]
static class Patch_Player_Awake
{
	private static readonly string VrmPath = Environment.CurrentDirectory + @"\ValheimVRM\player.vrm";
	private static Dictionary<string, GameObject> vrmDic = new Dictionary<string, GameObject>();

	[HarmonyPostfix]
	static void Postfix(Player __instance)
	{
		var playerName = Game.instance != null ? Game.instance.GetPlayerProfile().GetName() : null;
		Debug.Log(playerName);
		if (!string.IsNullOrEmpty(playerName) && !vrmDic.ContainsKey(playerName))
		{
			var path = Environment.CurrentDirectory + $"/ValheimVRM/{playerName}.vrm";

			if (!File.Exists(path))
			{
				Debug.LogError("VRMファイルが見つかりません。");
				Debug.LogError("読み込み予定だったVRMファイルパス: " + path);
			}
			else
			{
				var orgVrm = ImportVRM(path);
				orgVrm = ImportVRM(path);
				GameObject.DontDestroyOnLoad(orgVrm);
				vrmDic[playerName] = orgVrm;

				//[Error: Unity Log] _Cutoff: Range
				//[Error: Unity Log] _MainTex: Texture
				//[Error: Unity Log] _SkinBumpMap: Texture
				//[Error: Unity Log] _SkinColor: Color
				//[Error: Unity Log] _ChestTex: Texture
				//[Error: Unity Log] _ChestBumpMap: Texture
				//[Error: Unity Log] _ChestMetal: Texture
				//[Error: Unity Log] _LegsTex: Texture
				//[Error: Unity Log] _LegsBumpMap: Texture
				//[Error: Unity Log] _LegsMetal: Texture
				//[Error: Unity Log] _BumpScale: Float
				//[Error: Unity Log] _Glossiness: Range
				//[Error: Unity Log] _MetalGlossiness: Range

				// シェーダ差し替え
				var shader = Shader.Find("Custom/Player");
				foreach (var smr in orgVrm.GetComponentsInChildren<SkinnedMeshRenderer>())
				{
					foreach (var mat in smr.materials)
					{

						if (mat.shader == shader) continue;

						var color = mat.GetColor("_Color");

						var mainTex = mat.GetTexture("_MainTex") as Texture2D;
						var tex = new Texture2D(mainTex.width, mainTex.height);
						var colors = mainTex.GetPixels();
						for (var i = 0; i < colors.Length; i++)
						{
							var col = colors[i] * color;
							//colors[i] = col * 0.8f;
							//colors[i].a = col.a;
							float h, s, v;
							Color.RGBToHSV(col, out h, out s, out v);
							v *= 0.75f;
							colors[i] = Color.HSVToRGB(h, s, v);
							colors[i].a = col.a;
						}
						tex.SetPixels(colors);
						tex.Apply();

						var bumpMap = mat.GetTexture("_BumpMap");
						//var bumpScale = mat.GetFloat("_BumpScale");
						//var cutoff = mat.GetFloat("_Cutoff");

						//var shadeColor = mat.GetColor("_ShadeColor");

						mat.shader = shader;

						mat.SetTexture("_MainTex", tex);
						mat.SetTexture("_SkinBumpMap", bumpMap);
						mat.SetColor("_SkinColor", color);
						mat.SetTexture("_ChestTex", tex);
						mat.SetTexture("_ChestBumpMap", bumpMap);
						mat.SetTexture("_LegsTex", tex);
						mat.SetTexture("_LegsBumpMap", bumpMap);
						mat.SetFloat("_Glossiness", 0.2f);
						mat.SetFloat("_MetalGlossiness", 0.0f);
					}
				}

				orgVrm.SetActive(false);
			}
		}

		if (!string.IsNullOrEmpty(playerName) && vrmDic.ContainsKey(playerName))
		{
			var vrmModel = GameObject.Instantiate(vrmDic[playerName]);
			vrmModel.SetActive(true);
			vrmModel.transform.SetParent(__instance.GetComponentInChildren<Animator>().transform.parent, false);

			foreach (var smr in __instance.GetVisual().GetComponentsInChildren<SkinnedMeshRenderer>())
			{
				smr.forceRenderingOff = true;
				smr.updateWhenOffscreen = true;
			}

			//var dynMethod = __instance.GetType().GetMethod("SetVisible", BindingFlags.NonPublic | BindingFlags.Instance);
			//dynMethod.Invoke(__instance, new object[] { false });

			var orgAnim = AccessTools.FieldRefAccess<Player, Animator>(__instance, "m_animator");
			orgAnim.keepAnimatorControllerStateOnDisable = true;
			orgAnim.cullingMode = AnimatorCullingMode.AlwaysAnimate;

			vrmModel.transform.localPosition = orgAnim.transform.localPosition;

			if (vrmModel.GetComponent<VRMAnimationSync>() == null) vrmModel.AddComponent<VRMAnimationSync>().Setup(orgAnim);
			else vrmModel.GetComponent<VRMAnimationSync>().Setup(orgAnim);
		}
	}

	private static GameObject ImportVRM(string path)
	{
		try
		{
			// 1. GltfParser を呼び出します。
			//    GltfParser はファイルから JSON 情報とバイナリデータを読み出します。
			var parser = new GltfParser();
			parser.ParsePath(path);

			// 2. GltfParser のインスタンスを引数にして VRMImporterContext を作成します。
			//    VRMImporterContext は VRM のロードを実際に行うクラスです。
			using (var context = new VRMImporterContext(parser))
			{
				// 3. Load 関数を呼び出し、VRM の GameObject を生成します。
				context.Load();

				// 4. （任意） SkinnedMeshRenderer の UpdateWhenOffscreen を有効にできる便利関数です。
				context.EnableUpdateWhenOffscreen();

				// 5. VRM モデルを表示します。
				context.ShowMeshes();

				// 6. VRM の GameObject が実際に使用している UnityEngine.Object リソースの寿命を VRM の GameObject に紐付けます。
				//    つまり VRM の GameObject の破棄時に、実際に使用しているリソース (Texture, Material, Mesh, etc) をまとめて破棄することができます。
				context.DisposeOnGameObjectDestroyed();

				context.Root.transform.localScale *= 1.1f;

				Debug.Log("VRM読み込み成功");
				Debug.Log("VRMファイルパス: " + path);

				// 7. Root の GameObject を return します。
				//    Root の GameObject とは VRMMeta コンポーネントが付与されている GameObject のことです。
				return context.Root;
			}
		}
		catch (Exception ex)
		{
			Debug.LogError(ex);
		}

		return null;
	}
}

[DefaultExecutionOrder(int.MaxValue)]
public class VRMAnimationSync : MonoBehaviour
{
	private Animator orgAnim, vrmAnim;
	private HumanPoseHandler orgPose, vrmPose;
	private HumanPose hp = new HumanPose();
	private float height = 0.0f;

	public void Setup(Animator orgAnim)
	{
		this.orgAnim = orgAnim;
		this.vrmAnim = GetComponent<Animator>();
		this.vrmAnim.applyRootMotion = true;
		this.vrmAnim.updateMode = orgAnim.updateMode;
		this.vrmAnim.feetPivotActive = orgAnim.feetPivotActive;
		this.vrmAnim.layersAffectMassCenter = orgAnim.layersAffectMassCenter;
		this.vrmAnim.stabilizeFeet = orgAnim.stabilizeFeet;
		
		PoseHandlerCreate(orgAnim, vrmAnim);
	}

	void PoseHandlerCreate(Animator org, Animator vrm)
	{
		OnDestroy();
		orgPose = new HumanPoseHandler(org.avatar, org.transform);
		vrmPose = new HumanPoseHandler(vrm.avatar, vrm.transform);

		height = vrmAnim.GetBoneTransform(HumanBodyBones.Hips).position.y - orgAnim.GetBoneTransform(HumanBodyBones.Hips).position.y;
	}

	void OnDestroy()
	{
		if (orgPose != null)
			orgPose.Dispose();
		if (vrmPose != null)
			vrmPose.Dispose();
	}

	void Update()
	{
		for (var i = 0; i < 55; i++)
		{
			var orgTrans = orgAnim.GetBoneTransform((HumanBodyBones)i);
			var vrmTrans = vrmAnim.GetBoneTransform((HumanBodyBones)i);

			if (i > 0 && orgTrans != null && vrmTrans != null)
			{
				orgTrans.position = vrmTrans.position;
			}
		}
	}

	void LateUpdate()
	{
		orgPose.GetHumanPose(ref hp);
		vrmPose.SetHumanPose(ref hp);

		var posY = orgAnim.GetBoneTransform(HumanBodyBones.Hips).position.y;

		for (var i = 0; i < 55; i++)
		{
			var orgTrans = orgAnim.GetBoneTransform((HumanBodyBones)i);
			var vrmTrans = vrmAnim.GetBoneTransform((HumanBodyBones)i);

			if (i > 0 && orgTrans != null && vrmTrans != null)
			{
				orgTrans.position = vrmTrans.position;
			}
		}

		var pos = vrmAnim.transform.position;
		pos.y = posY + height;
		vrmAnim.transform.position = pos;
	}
}
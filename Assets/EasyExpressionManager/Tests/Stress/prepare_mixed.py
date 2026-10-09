import json,pathlib
base=json.loads(pathlib.Path(__file__).with_name('native-stress.json').read_text())['code']
prefix=base[:base.index('try {\nvar root')]
suffix=base[base.index('} catch(System.Exception ex)'):].replace('foreach(var m in menus)if(m)UnityEngine.Object.DestroyImmediate(m);','foreach(var m in menus)if(m && !EditorUtility.IsPersistent(m))UnityEngine.Object.DestroyImmediate(m);')
body=r'''
try {
var root=menu("MixedRoot");
for(int i=0;i<4;i++){var child=menu("Folder"+i);for(int j=0;j<5;j++)child.controls.Add(new VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control{name="T"+i+"_"+j,type=VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType.Toggle});root.controls.Add(new VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control{name="F"+i,type=VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType.SubMenu,subMenu=child});}
da.expressionsMenu=root;db.expressionsMenu=root;invoke(w,"Scan",null);
var rows=(System.Collections.Generic.List<ExpressionNode>)type.GetField("allNodesFlatList",flags).GetValue(w);
invoke(w,"DuplicateVRCNativeControl",new object[]{rows.Find(n=>n.rawName=="F0"),"Copy"});invoke(w,"Scan",null);
check("Folder duplicate independent submenu",da.expressionsMenu.controls[0].subMenu!=da.expressionsMenu.controls[1].subMenu && root.controls.Count==4,"private root controls="+da.expressionsMenu.controls.Count);
var rng=new System.Random(1847);int completed=0;var timer=System.Diagnostics.Stopwatch.StartNew();
for(int i=0;i<100;i++){
invoke(w,"Scan",null);rows=(System.Collections.Generic.List<ExpressionNode>)type.GetField("allNodesFlatList",flags).GetValue(w);
var toggles=rows.FindAll(n=>!n.IsFolderLike);var folders=rows.FindAll(n=>n.IsFolderLike);
var target=toggles[rng.Next(toggles.Count)];var dest=folders[rng.Next(folders.Count)];var destPath=(string)invoke(w,"PathOf",new object[]{dest,false});
var sourceControl=target.sourceControl;var leaf=target.rawName;
Undo.IncrementCurrentGroup();
try{invoke(w,"PerformMove",new object[]{target,destPath,dest,false});invoke(w,"Scan",null);rows=(System.Collections.Generic.List<ExpressionNode>)type.GetField("allNodesFlatList",flags).GetValue(w);var at=rows.Find(n=>n.rawName==leaf && (string)invoke(w,"PathOf",new object[]{n.parent,false})==destPath);if(at==null){results.Add("FAIL mixed move iteration="+i+" leaf="+leaf+" destination="+destPath);break;}completed++;}
catch(System.Exception ex){results.Add("FAIL mixed move iteration="+i+" "+ex.GetBaseException().ToString());break;}
}
timer.Stop();check("100 seeded mixed moves",completed==100,"completed="+completed+" ms="+timer.ElapsedMilliseconds);
var ft=(System.Type)invoke(w,"FindFeatureType",new object[]{"FullController"});var ct=(System.Type)invoke(w,"ConcreteVrcfType",null);
var fcMenu=menu("SharedFC");fcMenu.controls.Add(new VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control{name="FCItem",type=VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType.Toggle});AssetDatabase.CreateAsset(fcMenu,assets+"/SharedFC.asset");
var fc=a.AddComponent(ct);var feature=System.Activator.CreateInstance(ft);var entries=(System.Collections.IList)ft.GetField("menus").GetValue(feature);entries.Add(System.Activator.CreateInstance(ft.GetNestedType("MenuEntry")));var so=new SerializedObject(fc);so.FindProperty("content").managedReferenceValue=feature;so.ApplyModifiedPropertiesWithoutUndo();so.Update();so.FindProperty("content.menus").GetArrayElementAtIndex(0).FindPropertyRelative("menu.objRef").objectReferenceValue=fcMenu;so.ApplyModifiedPropertiesWithoutUndo();invoke(w,"Scan",null);
rows=(System.Collections.Generic.List<ExpressionNode>)type.GetField("allNodesFlatList",flags).GetValue(w);invoke(w,"DoRename",new object[]{rows.Find(n=>n.rawName=="FCItem"),"FCPrivate"});invoke(w,"Scan",null);
check("Full Controller rename isolates shared source",fcMenu.controls[0].name=="FCItem","original="+fcMenu.controls[0].name);
UnityEngine.Object.DestroyImmediate(fc);
var toggleType=(System.Type)invoke(w,"FindFeatureType",new object[]{"Toggle"});
var clothing=new GameObject("NestedClothing");UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(clothing,scene);var clothingPrefab=PrefabUtility.SaveAsPrefabAsset(clothing,assets+"/Clothing.prefab");UnityEngine.Object.DestroyImmediate(clothing);
var avatarObject=new GameObject("PrefabAvatar");UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(avatarObject,scene);avatarObject.AddComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();var nested=(GameObject)PrefabUtility.InstantiatePrefab(clothingPrefab,scene);nested.transform.SetParent(avatarObject.transform,false);var toggle=avatarObject.AddComponent(ct);so=new SerializedObject(toggle);feature=System.Activator.CreateInstance(toggleType);toggleType.GetField("name").SetValue(feature,"Folder/PrefabToggle");so.FindProperty("content").managedReferenceValue=feature;so.ApplyModifiedPropertiesWithoutUndo();
var prefab=PrefabUtility.SaveAsPrefabAsset(avatarObject,assets+"/Avatar.prefab");UnityEngine.Object.DestroyImmediate(avatarObject);var instance=(GameObject)PrefabUtility.InstantiatePrefab(prefab,scene);type.GetField("avatar",flags).SetValue(w,instance);type.GetField("cachedAvatarKey",flags).SetValue(w,null);invoke(w,"Scan",null);
rows=(System.Collections.Generic.List<ExpressionNode>)type.GetField("allNodesFlatList",flags).GetValue(w);invoke(w,"DoRename",new object[]{rows.Find(n=>n.rawName=="PrefabToggle"),"EditedToggle"});
check("Toggle rename preserves outer prefab",PrefabUtility.IsPartOfPrefabInstance(instance),"outer prefab connection");check("Toggle rename preserves nested clothing",PrefabUtility.IsPartOfPrefabInstance(instance.transform.GetChild(0).gameObject),"nested prefab connection");
invoke(w,"Scan",null);rows=(System.Collections.Generic.List<ExpressionNode>)type.GetField("allNodesFlatList",flags).GetValue(w);
check("Prefab toggle rename persisted",rows.Exists(n=>n.rawName=="EditedToggle"),"rescan finds EditedToggle");
var state=(EEMAvatarState)invoke(w,"AvatarState",null);AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(state));type.GetField("avatar",flags).SetValue(w,a);type.GetField("cachedAvatarKey",flags).SetValue(w,null);
var snapshots=(string)invoke(w,"SnapshotFolder",null);AssetDatabase.DeleteAsset(snapshots);state=(EEMAvatarState)invoke(w,"AvatarState",null);AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(state));
'''
pathlib.Path(__file__).with_name('mixed-stress.json').write_text(json.dumps({'action':'execute','code':prefix+body+suffix,'safety_checks':False}),encoding='utf-8')

import json, pathlib
base=json.loads(pathlib.Path(__file__).with_name('native-stress.json').read_text())['code']
prefix=base[:base.index('try {\nvar root')]
suffix=base[base.index('} catch(System.Exception ex)'):]
body=r'''
try {
var source=menu("PackageSimulation");
source.controls.Add(new VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control{name="OldPackageItem",type=VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType.Toggle});
AssetDatabase.CreateAsset(source,assets+"/PackageSimulation.asset");
da.expressionsMenu=source;db.expressionsMenu=source;invoke(w,"Scan",null);
var snapshot=(System.Collections.Generic.Dictionary<string,object>)invoke(w,"BuildSnapshotPayload",new object[]{"Stress",false});
source.controls[0].name="UpdatedPackageItem";EditorUtility.SetDirty(source);
var problems=new System.Collections.Generic.List<string>();
invoke(w,"RestoreMenuAssets",new object[]{snapshot,problems});
check("Restore leaves updated shared package intact",source.controls[0].name=="UpdatedPackageItem" && db.expressionsMenu==source,"package="+source.controls[0].name);
check("Restore uses old data privately",da.expressionsMenu!=source && da.expressionsMenu.controls[0].name=="OldPackageItem" && problems.Count==0,"restored="+da.expressionsMenu.controls[0].name+" warnings="+problems.Count);
invoke(w,"Scan",null);
var rows=(System.Collections.Generic.List<ExpressionNode>)type.GetField("allNodesFlatList",flags).GetValue(w);
var icon=new Texture2D(2,2);
invoke(w,"ApplyIconChange",new object[]{rows[0],icon});
check("Native icon isolated",source.controls[0].icon==null && da.expressionsMenu.controls[0].icon==icon,"shared icon unchanged");
invoke(w,"Scan",null);rows=(System.Collections.Generic.List<ExpressionNode>)type.GetField("allNodesFlatList",flags).GetValue(w);
invoke(w,"DeleteNode",new object[]{rows[0]});
check("Native delete isolated",source.controls.Count==1 && da.expressionsMenu.controls.Count==0,"source="+source.controls.Count+" private="+da.expressionsMenu.controls.Count);
UnityEngine.Object.DestroyImmediate(icon);
da.expressionsMenu=source;invoke(w,"Scan",null);
var snapshotDir=(string)invoke(w,"SnapshotFolder",null);
var baseline=(string)invoke(w,"WriteSnapshot",new object[]{"stress baseline",true,false});
var named=(string)invoke(w,"WriteSnapshot",new object[]{"stress named",false,false});
var timer=System.Diagnostics.Stopwatch.StartNew();
for(int i=0;i<25;i++)invoke(w,"WriteSnapshot",new object[]{"stress automatic "+i,false,true});
timer.Stop();
var snapshotFiles=System.IO.Directory.GetFiles(snapshotDir,"*.json");int autos=0;foreach(var f in snapshotFiles)if(System.IO.Path.GetFileName(f).StartsWith("auto_"))autos++;
check("25 automatic snapshots bounded",autos==10 && System.IO.File.Exists(baseline) && System.IO.File.Exists(named),"auto="+autos+" total="+snapshotFiles.Length+" ms="+timer.ElapsedMilliseconds);
var ft=(System.Type)invoke(w,"FindFeatureType",new object[]{"MoveMenuItem"});
var ct=(System.Type)invoke(w,"ConcreteVrcfType",null);
var manager=new GameObject("ExpressionsManager");manager.transform.SetParent(a.transform,false);
for(int i=0;i<2;i++){var c=manager.AddComponent(ct);var so=new SerializedObject(c);so.FindProperty("content").managedReferenceValue=System.Activator.CreateInstance(ft);so.ApplyModifiedPropertiesWithoutUndo();so.Update();so.FindProperty("content.fromPath").stringValue=i==0?"OldPackageItem":"Other";so.FindProperty("content.toPath").stringValue=i==0?"Other":"OldPackageItem";so.ApplyModifiedPropertiesWithoutUndo();}
var upload=new EEMMoveBuildOrder();timer.Restart();bool permitted=upload.OnPreprocessAvatar(a);timer.Stop();
check("Upload cyclic records does not block",permitted,"permitted="+permitted+" ms="+timer.ElapsedMilliseconds);
UnityEngine.Object.DestroyImmediate(manager);
var malformed=menu("NullControls");malformed.controls.Add(null);malformed.controls.Add(new VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control{name=null,type=VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType.Toggle});da.expressionsMenu=malformed;
try{invoke(w,"Scan",null);check("Null entries scan",true,"completed");}catch(System.Exception ex){check("Null entries scan",false,ex.GetBaseException().ToString());}
try{invoke(w,"BuildSnapshotPayload",new object[]{"Null",false});check("Null entries snapshot",true,"completed");}catch(System.Exception ex){check("Null entries snapshot",false,ex.GetBaseException().ToString());}
for(int depth=12;depth<=16;depth+=2){
var levels=new System.Collections.Generic.List<VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu>();
for(int i=0;i<=depth;i++)levels.Add(menu("StressDAG"+depth+"_"+i));
levels[depth].controls.Add(new VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control{name="Leaf",type=VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType.Toggle});
for(int i=0;i<depth;i++)for(int j=0;j<2;j++)levels[i].controls.Add(new VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control{name="B"+j,type=VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType.SubMenu,subMenu=levels[i+1]});
da.expressionsMenu=levels[0];var memoryBefore=System.GC.GetTotalMemory(true);timer.Restart();invoke(w,"Scan",null);timer.Stop();
rows=(System.Collections.Generic.List<ExpressionNode>)type.GetField("allNodesFlatList",flags).GetValue(w);
results.Add("MEASURE shared DAG depth="+depth+" assets="+levels.Count+" rows="+rows.Count+" ms="+timer.ElapsedMilliseconds+" managed_bytes_delta="+(System.GC.GetTotalMemory(false)-memoryBefore));
if(timer.ElapsedMilliseconds>15000)break;
}
AssetDatabase.DeleteAsset(snapshotDir);
var state=(EEMAvatarState)invoke(w,"AvatarState",null);AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(state));
'''
suffix=suffix.replace('foreach(var m in menus)if(m)UnityEngine.Object.DestroyImmediate(m);','foreach(var m in menus)if(m && !EditorUtility.IsPersistent(m))UnityEngine.Object.DestroyImmediate(m);')
pathlib.Path(__file__).with_name('adversarial-stress.json').write_text(json.dumps({'action':'execute','code':prefix+body+suffix,'safety_checks':False}),encoding='utf-8')

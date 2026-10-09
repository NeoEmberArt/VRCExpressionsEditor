import json, pathlib

code = r'''
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
var type = typeof(EASYExpressionManager);
System.Func<object,string,object[],object> invoke = (obj,name,args) => type.GetMethod(name,flags).Invoke(obj,args);
var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
var w = ScriptableObject.CreateInstance<EASYExpressionManager>();
type.GetMethod("OnDisable",flags).Invoke(w,null);
var assets = "Assets/EasyExpressionManager/Tests/Stress/Generated";
if (!AssetDatabase.IsValidFolder(assets)) AssetDatabase.CreateFolder("Assets/EasyExpressionManager/Tests/Stress","Generated");
type.GetField("regressionCloneRoot",flags).SetValue(w,assets+"/Clones");
var a = new GameObject("EEM_Stress_A"); var b = new GameObject("EEM_Stress_B");
UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(a,scene);
UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(b,scene);
var da = a.AddComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
var db = b.AddComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
type.GetField("avatar",flags).SetValue(w,a);
var results = new System.Collections.Generic.List<string>();
System.Action<string,bool,string> check = (name,ok,detail) => results.Add((ok?"PASS ":"FAIL ")+name+" | "+detail);
var menus = new System.Collections.Generic.List<VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu>();
System.Func<string,VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu> menu = name => { var m = ScriptableObject.CreateInstance<VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu>();m.name=name;menus.Add(m);return m;};
try {
var root = menu("SharedRoot"); var sub = menu("SharedChild");
sub.controls.Add(new VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control {name="Toggle",type=VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType.Toggle});
root.controls.Add(new VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control {name="Left",type=VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType.SubMenu,subMenu=sub});
root.controls.Add(new VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control {name="Right",type=VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType.SubMenu,subMenu=sub});
da.expressionsMenu=root;db.expressionsMenu=root;invoke(w,"Scan",null);
var rows = (System.Collections.Generic.List<ExpressionNode>)type.GetField("allNodesFlatList",flags).GetValue(w);
check("Repeated submenu scan",rows.Count==4,"rows="+rows.Count);
var target = rows.Find(n=>n.rawName=="Toggle");
invoke(w,"DoRename",new object[]{target,"Renamed"});
check("Native copy isolates other avatar",db.expressionsMenu==root && sub.controls[0].name=="Toggle" && da.expressionsMenu!=root,"shared original="+sub.controls[0].name);
check("Repeated submenu aliases preserved",da.expressionsMenu.controls[0].subMenu==da.expressionsMenu.controls[1].subMenu,"both aliases reference same private submenu");
invoke(w,"Scan",null);rows=(System.Collections.Generic.List<ExpressionNode>)type.GetField("allNodesFlatList",flags).GetValue(w);
check("Renamed alias rows synchronized",rows.FindAll(n=>n.rawName=="Renamed").Count==2,"renamed aliases="+rows.FindAll(n=>n.rawName=="Renamed").Count);
var stale = rows.Find(n=>n.rawName=="Renamed");
type.GetField("externalChangesPending",flags).SetValue(w,true);
invoke(w,"DoRename",new object[]{stale,"ShouldBeRefused"});
check("Stale action refused",da.expressionsMenu.controls[0].subMenu.controls[0].name=="Renamed","native name="+da.expressionsMenu.controls[0].subMenu.controls[0].name);
Undo.IncrementCurrentGroup();
int group=Undo.GetCurrentGroup();
var before=da.expressionsMenu.controls[0].name;
System.Action failure=()=>{Undo.RecordObject(da.expressionsMenu,"Stress rollback");da.expressionsMenu.controls[0].name="Corrupted";throw new System.InvalidOperationException("Injected failure");};
var ok=(bool)invoke(w,"LogOp",new object[]{"Injected","Left","Folder","Left","Corrupted",failure,null});
check("Injected failure rolls back",!ok && da.expressionsMenu.controls[0].name==before,"name="+da.expressionsMenu.controls[0].name);
var cycle=menu("Cycle");cycle.controls.Add(new VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control{name="Self",type=VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType.SubMenu,subMenu=cycle});
da.expressionsMenu=cycle;
var timer=System.Diagnostics.Stopwatch.StartNew();invoke(w,"Scan",null);timer.Stop();
rows=(System.Collections.Generic.List<ExpressionNode>)type.GetField("allNodesFlatList",flags).GetValue(w);
check("Self cycle terminates",rows.Count==1,"rows="+rows.Count+", ms="+timer.ElapsedMilliseconds);
invoke(w,"DoRename",new object[]{rows[0],"SelfRenamed"});
check("Copy preserves self cycle",da.expressionsMenu.controls[0].subMenu==da.expressionsMenu,"clone self reference intact");
var wide=menu("Wide");for(int i=0;i<2048;i++)wide.controls.Add(new VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control{name="T"+i,type=VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType.Toggle});
da.expressionsMenu=wide;timer.Restart();invoke(w,"Scan",null);timer.Stop();
rows=(System.Collections.Generic.List<ExpressionNode>)type.GetField("allNodesFlatList",flags).GetValue(w);
check("2048 controls scan",rows.Count==2048,"rows="+rows.Count+", ms="+timer.ElapsedMilliseconds);
var levels=new System.Collections.Generic.List<VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu>();
for(int i=0;i<11;i++)levels.Add(menu("DAG"+i));
levels[10].controls.Add(new VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control{name="Leaf",type=VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType.Toggle});
for(int i=0;i<10;i++)for(int j=0;j<2;j++)levels[i].controls.Add(new VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control{name="Branch"+j,type=VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType.SubMenu,subMenu=levels[i+1]});
da.expressionsMenu=levels[0];timer.Restart();invoke(w,"Scan",null);timer.Stop();
rows=(System.Collections.Generic.List<ExpressionNode>)type.GetField("allNodesFlatList",flags).GetValue(w);
check("Shared DAG expansion depth10",rows.Count==3070,"11 menu assets produce "+rows.Count+" rows; ms="+timer.ElapsedMilliseconds);
var deep=menu("DeepRoot");var cursor=deep;for(int i=0;i<128;i++){var next=menu("Depth"+i);cursor.controls.Add(new VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control{name="D"+i,type=VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType.SubMenu,subMenu=next});cursor=next;}
da.expressionsMenu=deep;timer.Restart();invoke(w,"Scan",null);timer.Stop();
rows=(System.Collections.Generic.List<ExpressionNode>)type.GetField("allNodesFlatList",flags).GetValue(w);
check("Depth128 terminates",rows.Count==128,"rows="+rows.Count+", ms="+timer.ElapsedMilliseconds);
check("Upload null avatar permits build",new EEMMoveBuildOrder().OnPreprocessAvatar(null),"true");
check("Upload without records permits build",new EEMMoveBuildOrder().OnPreprocessAvatar(a),"true");
} catch(System.Exception ex){results.Add("HARNESS ERROR "+ex.ToString());}
finally {type.GetField("avatar",flags).SetValue(w,null);UnityEngine.Object.DestroyImmediate(w);UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);foreach(var m in menus)if(m)UnityEngine.Object.DestroyImmediate(m);}
return string.Join("\n",results);
'''
pathlib.Path(__file__).with_name('native-stress.json').write_text(json.dumps({'action':'execute','code':code}),encoding='utf-8')

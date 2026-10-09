using System;
using System.Collections.Generic;
using UnityEngine;

// Editor asset: no component is added to the avatar or its uploaded hierarchy.
public sealed class EEMAvatarState : ScriptableObject
{
    public string avatarKey;
    public List<string> ownerIds = new List<string>();
    public List<string> legacyKeys = new List<string>();
    public List<string> virtualFolders = new List<string>();
    public List<EEMAddedComponent> additions = new List<EEMAddedComponent>();
    public List<string> keptMenuRecordKeys = new List<string>();
}

[Serializable]
public sealed class EEMAddedComponent
{
    public string stableId;
    public string session;
    public int instanceId;
}

﻿/**
 * Google Sync Plugin for KeePass Password Safe
 * Copyright(C) 2012-2016  DesignsInnovate
 * Copyright(C) 2014-2016  Paul Voegler
 * 
 * KeePass Sync for Google Drive
 * Copyright(C) 2020       Walter Goodwin
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
**/

using Google.Apis.Drive.v3;
using KeePassLib;
using KeePassLib.Collections;
using KeePassLib.Security;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace KeePassSyncForDrive
{
    public abstract class SyncConfiguration
    {
        // Ver0
        //	clientID/secret present => custom client id
        //	clientID/secret missing => legacy client id
        //
        // Ver1
        //	if Use legacy flag missing or false
        //	 => new app client id
        //  else
        //	 clientID/secret present => custom client id
        //	 clientID/secret missing => legacy client id
        //

        public const string Ver0 = "0.0"; // virtual version
        public const string Ver1 = "1.0";

        protected const string CurrentVer = Ver1;

        public static Version CurrentVersion
        {
            get { return new Version(CurrentVer); }
        }

        public static SyncConfiguration GetEmpty()
        {
            return new TransientConfiguration();
        }

        public static bool IsEmpty(string clientId,
            ProtectedString clientSecret)
        {
            return string.IsNullOrEmpty(clientId) ||
                clientSecret == null ||
                clientSecret.IsEmpty;
        }

        public bool IsEmptyOauthCredentials
        {
            get
            {
                return IsEmpty(ClientId, ClientSecret);
            }
        }

        // KeePass string fields.
        public abstract string Title { get; }
        public abstract string User { get; }
        public abstract ProtectedString Password { get; }
        public abstract string LoginHint { get; }

        // Plugin string fields.
        public abstract bool? ActiveAccount { get; set; }
        public abstract string ClientId { get; set; }
        public abstract ProtectedString ClientSecret { get; set; }
        public abstract ProtectedString RefreshToken { get; set; }
        public abstract string ActiveFolder { get; set; }
        public abstract string LegacyDriveScope { get; set; }
        public abstract bool UseLegacyCreds { get; set; }
        public abstract Version Version { get; }
    }

    class TransientConfiguration : SyncConfiguration
    {
        static ProtectedString NullOrCopy(ProtectedString copyee)
        {
            if (copyee == null)
            {
                return null;
            }
            return new ProtectedString(copyee.IsProtected, copyee.ReadString());
        }

        readonly string m_title;
        readonly string m_user;
        readonly ProtectedString m_password;
        readonly string m_loginHint;
        readonly Version m_ver;

        public TransientConfiguration()
        {
            ActiveAccount = null;
            m_user = null;
            m_password = null;
            m_loginHint = null;
            m_title = null;
            ClientId = null;
            ClientSecret = null;
            RefreshToken = null;
            ActiveFolder = null;
            LegacyDriveScope = null;
            m_ver = new Version(CurrentVer);
        }

        public TransientConfiguration(SyncConfiguration copyee)
        {
            ActiveAccount = copyee.ActiveAccount;
            m_user = copyee.User;
            m_password = NullOrCopy(copyee.Password);
            ClientId = copyee.ClientId;
            ClientSecret = NullOrCopy(copyee.ClientSecret);
            RefreshToken = NullOrCopy(copyee.RefreshToken);
            m_loginHint = copyee.LoginHint;
            m_title = copyee.Title;
            ActiveFolder = copyee.ActiveFolder;
            LegacyDriveScope = copyee.LegacyDriveScope;
            m_ver = copyee.Version;
        }

        public override bool? ActiveAccount { get; set; }

        public override string Title
        {
            get
            {
                return m_title;
            }
        }

        public override string User
        {
            get
            {
                return m_user;
            }
        }

        public override ProtectedString Password
        {
            get
            {
                return m_password;
            }
        }

        public override string LoginHint
        {
            get
            {
                return m_loginHint;
            }
        }

        public override string ClientId { get; set; }

        public override ProtectedString ClientSecret { get; set; }

        public override ProtectedString RefreshToken { get; set; }

        public override string ActiveFolder { get; set; }
        
        public override bool UseLegacyCreds { get; set; }

        public override string LegacyDriveScope { get; set; }

        public override Version Version 
        {
            get { return m_ver; }
        }
    }

    /// <summary>
    /// Mirror, and defer incremental modifications to, PwEntry object
    /// "Strings" properties.  This mainly serves as the data model
    /// for the PwEntry-backed properties on the configuration dialog.
    /// </summary>
    public class EntryConfiguration : SyncConfiguration
    {
        readonly Dictionary<string, object> m_changes;
        string m_title;
        bool m_changesCommitted;

        public EntryConfiguration(PwEntry entry)
        {
            Entry = entry;
            m_changes = new Dictionary<string, object>(5);
            m_title = null;
            m_changesCommitted = false;

            UseLegacyKp3ClientId = IsEmptyOauthCredentials;
            ChangesCommitted = false;
        }

        ProtectedStringDictionary Strings
        {
            get
            {
                return Entry.Strings;
            }
        }

        StringDictionaryEx CustomData
        {
            get
            {
                return Entry.CustomData;
            }
        }

        public PwEntry Entry { get; private set; }

        public PwEntry CommitChangesIfAny()
        {
            EnsureSettingsMigration();
            if (UseLegacyKp3ClientId)
            {
                // Traditionally, the plugin's indicator for "use default 
                // clientId" is empty strings for clientId & secret.  Maintain
                // that compatibility point.
                ClientId = string.Empty;
                ClientSecret = null;
            }
            if (IsModified)
            {
                foreach (KeyValuePair<string, object> kv in m_changes)
                {
                    switch (kv.Key)
                    {
                        case GdsDefs.EntryUseLegacyCreds:
                        case GdsDefs.EntryActiveAccount:
                        case GdsDefs.EntryClientId:
                        case GdsDefs.EntryActiveAppFolder:
                        case GdsDefs.EntryDriveScope:
                        case GdsDefs.EntryVersion:
                            string stringVal = kv.Value as string;
                            CustomData.Set(kv.Key, 
                                stringVal == null ? string.Empty : stringVal);
                            break;
                        case GdsDefs.EntryClientSecret:
                        case GdsDefs.EntryRefreshToken:
                            ProtectedString ps = kv.Value as ProtectedString;
                            CustomData.Set(kv.Key,
                                ps == null ? string.Empty : ps.ReadString());
                            break;
                        default:
                            Debug.Fail("Unknown key!!");
                            break;
                    }
                }
                m_changes.Clear();
                ChangesCommitted = true;
            }
            return Entry;
        }

        /// <summary>
        /// Indicates that there are changes that have not been committed to
        /// the Entry object yet.  The property is set and reset by
        /// incremental changes to the properties mirroring Entry.
        /// </summary>
        public bool IsModified
        {
            get
            {
                return m_changes.Keys.Any();
            }
        }

        /// <summary>
        /// Indicates there are uncommitted changes to the OAuth 2.0-related
        /// properties.
        /// </summary>
        public bool IsModifiedOauthCreds
        {
            get
            {
                return m_changes.Keys.Any(k => k == GdsDefs.EntryClientId ||
                                            k == GdsDefs.EntryClientSecret ||
                                            k == GdsDefs.EntryUseLegacyCreds);
            }
        }

        /// <summary>
        /// Indicates that changes have been made and committed to the Entry
        /// object.  You might want to save the database if Entry is contained
        /// in it, for example.  This property is set when IsModified is true
        /// when the CommitChangesIfAny method is called.  You can also set
        /// this as a flag in the constructor overload.
        /// </summary>
        public bool ChangesCommitted 
        {
            get
            {
                return m_changesCommitted;
            }
            private set
            {
                m_changesCommitted = value || m_changesCommitted;
                if (m_changesCommitted)
                {
                    Entry.Touch(true);
                }
            }
        }

        public override string LoginHint
        {
            get
            {
                return Strings.ReadSafe(PwDefs.UserNameField);
            }
        }

        public override string User
        {
            get
            {
                return Strings.ReadSafe(PwDefs.UserNameField);
            }
        }

        public override ProtectedString Password
        {
            get
            {
                return Strings.Get(PwDefs.PasswordField);
            }
        }

        public override string Title
        {
            get
            {
                if (m_title == null)
                {
                    m_title = Strings.ReadSafe(PwDefs.TitleField);
                    string userName = User;
                    if (!string.IsNullOrEmpty(userName))
                    {
                        return string.Format("{0} - {1}", userName, m_title);
                    }
                }
                return m_title;
            }
        }

        T GetValue<T>(string key, Func<string, T> getter) where T : class
        {
            object retVal;
            if (!m_changes.TryGetValue(key, out retVal))
            {
                retVal = getter(key);
            }
            return retVal as T;
        }

        void SetValue(string key, string value)
        {
            string curVal = ReadSafe(key);
            if (0 == string.CompareOrdinal(curVal, value))
            {
                m_changes.Remove(key);
            }
            else
            {
                m_changes[key] = value;
            }
        }

        void SetValue(string key, ProtectedString value)
        {
            ProtectedString curVal = Get(key);
            if (curVal != null)
            {
                if (value != null &&
                    curVal.OrdinalEquals(value, true))
                {
                    // Changed back to the original value.
                    m_changes.Remove(key);
                }
                else
                {
                    m_changes[key] = value;
                }
            }
            else if (value != null)
            {
                // Entry value null, given value non-null.
                m_changes[key] = value;
            }
            else
            {
                // Both null.
                m_changes.Remove(key);
            }
        }

        string ReadSafe(string key)
        {
            if (CustomData.Exists(key))
            {
                return CustomData.Get(key);
            }
            if (Strings.Exists(key))
            {
                // Copy to plug-in data area.
                string value = Strings.ReadSafe(key);
                CustomData.Set(key, value);
                ChangesCommitted = true;
                return value;
            }
            return string.Empty;
        }

        ProtectedString GetSafe(string key)
        {
            if (CustomData.Exists(key))
            {
                return new ProtectedString(true, CustomData.Get(key));
            }
            if (Strings.Exists(key))
            {
                // A requested key exists in the legacy location.  Get it from
                // CustomData from now on.
                ProtectedString value = Strings.Get(key);
                CustomData.Set(key, value.ReadString());
                ChangesCommitted = true;
                return value;
            }
            return ProtectedString.Empty;
        }

        ProtectedString Get(string key)
        {
            if (CustomData.Exists(key))
            {
                // For key users that may expect null values, an empty string
                // in CustomData will now imply the null value.
                string value = CustomData.Get(key);
                return string.IsNullOrEmpty(value) ? null :
                    new ProtectedString(true, value);
            }
            if (Strings.Exists(key))
            {
                // $$BUG
                // If this plugin is used SxS with the legacy plugin, this key
                // could relay changes made by the legacy that don't apply to 
                // this plugin; specifically, a previously null value in the 
                // legacy plugin could be changed to non-null, and subseqently
                // be retrieved here.

                // A requested key exists in the legacy location.  Get it from
                // CustomData from now on.
                ProtectedString value = Strings.Get(key);
                CustomData.Set(key, value.ReadString());
                ChangesCommitted = true;
                return value;
            }
            return null;
        }

        public override bool? ActiveAccount
        {
            get
            {
                string stringVal = GetValue(GdsDefs.EntryActiveAccount, ReadSafe);
                if (stringVal == GdsDefs.EntryActiveAccountFalse)
                {
                    return false;
                }
                else if (stringVal == GdsDefs.EntryActiveAccountTrue)
                {
                    return true;
                }
                return null;
            }
            set
            {
                if (!value.HasValue)
                {
                    SetValue(GdsDefs.EntryActiveAccount, (string)null);
                }
                else if (value.Value)
                {
                    SetValue(GdsDefs.EntryActiveAccount, GdsDefs.EntryActiveAccountTrue);
                }
                else // !value.Value
                {
                    SetValue(GdsDefs.EntryActiveAccount, GdsDefs.EntryActiveAccountFalse);
                }
            }
        }

        public override string ClientId
        {
            get
            {
                return GetValue(GdsDefs.EntryClientId, ReadSafe);
            }
            set
            {
                SetValue(GdsDefs.EntryClientId, value);
            }
        }

        public override ProtectedString ClientSecret
        {
            get
            {
                // Use GetSafe so this property can be bound to
                // SecureTextBoxEx.TextEx.
                return GetValue(GdsDefs.EntryClientSecret, GetSafe);
            }
            set
            {
                SetValue(GdsDefs.EntryClientSecret, value);
            }
        }

        public bool UseLegacyKp3ClientId { get; set; }
        
        public override ProtectedString RefreshToken
        {
            get
            {
                return GetValue(GdsDefs.EntryRefreshToken, Get);
            }
            set
            {
                SetValue(GdsDefs.EntryRefreshToken, value);
            }
        }

        public override string ActiveFolder
        {
            get
            {
                return GetValue(GdsDefs.EntryActiveAppFolder, ReadSafe);
            }
            set
            {
                SetValue(GdsDefs.EntryActiveAppFolder, value);
            }
        }

        public override string LegacyDriveScope
        {
            get
            {
                return GetValue(GdsDefs.EntryDriveScope, ReadSafe);
            }
            set
            {
                SetValue(GdsDefs.EntryDriveScope, value);
            }
        }

        public bool IsLegacyRestrictedDriveScope
        {
            get
            {
                return string.IsNullOrEmpty(LegacyDriveScope) ||
                    LegacyDriveScope == DriveService.Scope.Drive;
            }
            set
            {
                LegacyDriveScope = value ?
                    DriveService.Scope.Drive : DriveService.Scope.DriveFile;
            }
        }

        // Custom or KP3 app creds.
        public override bool UseLegacyCreds
        {
            get
            {
                return GdsDefs.ConfigTrue ==
                    GetValue(GdsDefs.EntryUseLegacyCreds, ReadSafe);
            }
            set
            {
                SetValue(GdsDefs.EntryUseLegacyCreds,
                    value ? GdsDefs.ConfigTrue : GdsDefs.ConfigFalse);
            }
        }

        public override Version Version
        {
            get
            {
                string strVer = GetValue(GdsDefs.EntryVersion, ReadSafe);
                Version retVal;
                if (!Version.TryParse(strVer, out retVal))
                {
                    retVal = new Version(Ver0);
                }
                return retVal;
            }
        }

        void EnsureSettingsMigration()
        {
            if (Version == CurrentVersion)
            {
                return;
            }

            // Only upgrade the version if all properties associated with the
            // particular version level are present.
            if (Version < new Version(Ver1) &&
                !string.IsNullOrEmpty(GetValue(GdsDefs.EntryUseLegacyCreds,
                                                    ReadSafe)))
            {
                SetValue(GdsDefs.EntryVersion, Ver1);
            }
        }
    }
}

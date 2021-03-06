﻿/*
Technitium Bit Chat
Copyright (C) 2015  Shreyas Zare (shreyas@technitium.com)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.

*/

using BitChatClient.FileSharing;
using BitChatClient.Network;
using BitChatClient.Network.Connections;
using BitChatClient.Network.KademliaDHT;
using BitChatClient.Network.SecureChannel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Threading;
using TechnitiumLibrary.Net.BitTorrent;
using TechnitiumLibrary.Security.Cryptography;

namespace BitChatClient
{
    public delegate void PeerNotification(BitChat sender, BitChat.Peer peer);
    public delegate void PeerHasRevokedCertificate(BitChat sender, InvalidCertificateException ex);
    public delegate void MessageReceived(BitChat.Peer sender, MessageItem message);
    public delegate void FileAdded(BitChat sender, SharedFile sharedFile);
    public delegate void PeerSecureChannelException(BitChat sender, SecureChannelException ex);
    public delegate void PeerHasChangedCertificate(BitChat sender, Certificate cert);

    public enum BitChatConnectivityStatus
    {
        NoNetwork = 0,
        PartialNetwork = 1,
        FullNetwork = 2
    }

    public class BitChat : IDisposable
    {
        #region events

        public event PeerNotification PeerAdded;
        public event PeerNotification PeerTyping;
        public event PeerHasRevokedCertificate PeerHasRevokedCertificate;
        public event PeerSecureChannelException PeerSecureChannelException;
        public event PeerHasChangedCertificate PeerHasChangedCertificate;
        public event MessageReceived MessageReceived;
        public event FileAdded FileAdded;
        public event EventHandler Leave;

        #endregion

        #region variables

        SynchronizationContext _syncCxt = SynchronizationContext.Current;

        IBitChatManager _manager;
        BitChatProfile _profile;
        BitChatNetwork _network;
        string _messageStoreID;
        byte[] _messageStoreKey;

        MessageStore _store;

        List<BitChat.Peer> _peers = new List<Peer>();
        Dictionary<BinaryID, SharedFile> _sharedFiles = new Dictionary<BinaryID, SharedFile>();

        //tracker
        TrackerManager _trackerManager;
        DhtClient _dhtClient;
        bool _enableTracking;

        //noop timer
        const int NOOP_MESSAGE_TIMER_INTERVAL = 15000;
        Timer _NOOPTimer;

        //network status
        Peer _selfPeer;
        List<PeerInfo> _connectedPeerList = new List<PeerInfo>();
        List<PeerInfo> _disconnectedPeerList = new List<PeerInfo>();
        BitChatConnectivityStatus _connectivityStatus = BitChatConnectivityStatus.NoNetwork;

        bool _updateNetworkStatusTriggered;
        bool _updateNetworkStatusRunning;
        object _updateNetworkStatusLock = new object();
        Timer _updateNetworkStatusTimer;
        Timer _reCheckNetworkStatusTimer; // to retry connection to disconnected peers
        const int NETWORK_STATUS_TIMER_INTERVAL = 1000;
        const int NETWORK_STATUS_RECHECK_TIMER_INTERVAL = 10000;

        #endregion

        #region constructor

        internal BitChat(IBitChatManager manager, ConnectionManager connectionManager, BitChatProfile profile, BitChatNetwork network, string messageStoreID, byte[] messageStoreKey, BitChatProfile.SharedFileInfo[] sharedFileInfoList, Uri[] trackerURIs, bool enableTracking)
        {
            _manager = manager;

            _profile = profile;
            _profile.ProxyUpdated += profile_ProxyUpdated;

            _network = network;
            _network.VirtualPeerAdded += network_VirtualPeerAdded;
            _network.VirtualPeerHasRevokedCertificate += network_VirtualPeerHasRevokedCertificate;
            _network.VirtualPeerSecureChannelException += network_VirtualPeerSecureChannelException;
            _network.VirtualPeerHasChangedCertificate += network_VirtualPeerHasChangedCertificate;

            _messageStoreID = messageStoreID;
            _messageStoreKey = messageStoreKey;

            string messageStoreFolder = Path.Combine(_profile.ProfileFolder, "messages");
            if (!Directory.Exists(messageStoreFolder))
                Directory.CreateDirectory(messageStoreFolder);

            _store = new MessageStore(new FileStream(Path.Combine(messageStoreFolder, messageStoreID + ".index"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), new FileStream(Path.Combine(messageStoreFolder, messageStoreID + ".data"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), _messageStoreKey);

            foreach (BitChatNetwork.VirtualPeer virtualPeer in _network.GetVirtualPeerList())
            {
                Peer peer = new Peer(virtualPeer, this);

                if (peer.IsSelf)
                    _selfPeer = peer;

                _peers.Add(peer);
            }

            foreach (BitChatProfile.SharedFileInfo info in sharedFileInfoList)
            {
                try
                {
                    _sharedFiles.Add(info.FileMetaData.FileID, SharedFile.LoadFile(info, this, _syncCxt));
                }
                catch
                { }
            }

            //init tracking
            _dhtClient = connectionManager.DhtClient;
            _trackerManager = new TrackerManager(_network.NetworkID, connectionManager.LocalPort, _dhtClient);
            _trackerManager.Proxy = _profile.Proxy;
            _trackerManager.DiscoveredPeers += trackerManager_DiscoveredPeers;
            _enableTracking = enableTracking;

            if (_network.Status == BitChatNetworkStatus.Offline)
            {
                _trackerManager.AddTracker(trackerURIs);
            }
            else
            {
                //start local peer discovery
                _manager.StartLocalTracking(_network.NetworkID);

                //enable tracking
                if (enableTracking)
                    _trackerManager.StartTracking(trackerURIs);
                else
                    _trackerManager.AddTracker(trackerURIs);
            }

            //start noop timer
            _NOOPTimer = new Timer(NOOPTimerCallback, null, NOOP_MESSAGE_TIMER_INTERVAL, Timeout.Infinite);

            //start network update timer
            _updateNetworkStatusTimer = new Timer(UpdateNetworkStatusCallback, null, NETWORK_STATUS_TIMER_INTERVAL, Timeout.Infinite);
            _reCheckNetworkStatusTimer = new Timer(ReCheckNetworkStatusCallback, null, Timeout.Infinite, Timeout.Infinite);
        }

        #endregion

        #region IDisposable

        ~BitChat()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        bool _disposed = false;

        private void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                //stop noop timer
                if (_NOOPTimer != null)
                {
                    _NOOPTimer.Dispose();
                    _NOOPTimer = null;
                }

                if (_updateNetworkStatusTimer != null)
                {
                    _updateNetworkStatusTimer.Dispose();
                    _updateNetworkStatusTimer = null;
                }

                if (_reCheckNetworkStatusTimer != null)
                {
                    _reCheckNetworkStatusTimer.Dispose();
                    _reCheckNetworkStatusTimer = null;
                }

                //stop tracking
                _manager.StopLocalTracking(_network.NetworkID);
                _trackerManager.Dispose();

                //stop network
                _network.Dispose();

                //stop shared files
                foreach (KeyValuePair<BinaryID, SharedFile> sharedFile in _sharedFiles)
                    sharedFile.Value.Dispose();

                //close message store
                _store.Dispose();

                _disposed = true;
            }
        }

        #endregion

        #region private event functions

        private void RaiseEventPeerAdded(Peer peer)
        {
            _syncCxt.Post(PeerAddedCallback, peer);
        }

        private void PeerAddedCallback(object state)
        {
            try
            {
                PeerAdded(this, state as Peer);
            }
            catch { }
        }

        private void RaiseEventPeerTyping(Peer peer)
        {
            _syncCxt.Send(PeerTypingCallback, peer);
        }

        private void PeerTypingCallback(object state)
        {
            try
            {
                PeerTyping(this, state as Peer);
            }
            catch { }
        }

        private void RaiseEventPeerHasRevokedCertificate(InvalidCertificateException ex)
        {
            _syncCxt.Post(PeerHasRevokedCertificateCallback, ex);
        }

        private void PeerHasRevokedCertificateCallback(object state)
        {
            try
            {
                PeerHasRevokedCertificate(this, state as InvalidCertificateException);
            }
            catch { }
        }

        private void RaiseEventPeerSecureChannelException(SecureChannelException ex)
        {
            _syncCxt.Post(PeerSecureChannelExceptionCallback, ex);
        }

        private void PeerSecureChannelExceptionCallback(object state)
        {
            try
            {
                PeerSecureChannelException(this, state as SecureChannelException);
            }
            catch { }
        }

        private void RaiseEventPeerHasChangedCertificate(Certificate cert)
        {
            _syncCxt.Post(PeerHasChangedCertificateCallback, cert);
        }

        private void PeerHasChangedCertificateCallback(object state)
        {
            try
            {
                PeerHasChangedCertificate(this, state as Certificate);
            }
            catch { }
        }

        private void RaiseEventMessageReceived(Peer peer, MessageItem message)
        {
            _syncCxt.Post(MessageReceivedCallback, new object[] { peer, message });
        }

        private void MessageReceivedCallback(object state)
        {
            try
            {
                MessageReceived((Peer)((object[])state)[0], (MessageItem)((object[])state)[1]);
            }
            catch { }
        }

        private void RaiseEventFileAdded(SharedFile file)
        {
            _syncCxt.Post(FileAddedCallback, file);
        }

        private void FileAddedCallback(object state)
        {
            try
            {
                FileAdded(this, state as SharedFile);
            }
            catch { }
        }

        private void RaiseEventLeave()
        {
            _syncCxt.Post(LeaveCallback, null);
        }

        private void LeaveCallback(object state)
        {
            try
            {
                Leave(this, EventArgs.Empty);
            }
            catch { }
        }

        #endregion

        #region public

        internal BitChatProfile.BitChatInfo GetBitChatInfo()
        {
            List<Certificate> peerCerts = new List<Certificate>();
            List<BitChatProfile.SharedFileInfo> sharedFileInfo = new List<BitChatProfile.SharedFileInfo>();

            lock (_peers)
            {
                foreach (BitChat.Peer peer in _peers)
                {
                    if (!peer.IsSelf)
                        peerCerts.Add(peer.PeerCertificate);
                }
            }

            lock (_sharedFiles)
            {
                foreach (KeyValuePair<BinaryID, SharedFile> sharedFile in _sharedFiles)
                {
                    if (sharedFile.Value.State != SharedFileState.Advertisement)
                        sharedFileInfo.Add(sharedFile.Value.GetSharedFileInfo());
                }
            }

            if (_network.Type == BitChatNetworkType.PrivateChat)
                return new BitChatProfile.BitChatInfo(BitChatNetworkType.PrivateChat, _network.PeerEmailAddress.Address, _network.SharedSecret, _network.NetworkID, _messageStoreID, _messageStoreKey, peerCerts.ToArray(), sharedFileInfo.ToArray(), _trackerManager.GetTracketURIs(), _enableTracking, _network.Status);
            else
                return new BitChatProfile.BitChatInfo(BitChatNetworkType.GroupChat, _network.NetworkName, _network.SharedSecret, _network.NetworkID, _messageStoreID, _messageStoreKey, peerCerts.ToArray(), sharedFileInfo.ToArray(), _trackerManager.GetTracketURIs(), _enableTracking, _network.Status);
        }

        public BitChat.Peer[] GetPeerList()
        {
            lock (_peers)
            {
                return _peers.ToArray();
            }
        }

        public SharedFile[] GetSharedFileList()
        {
            lock (_sharedFiles)
            {
                SharedFile[] sharedFilesList = new SharedFile[_sharedFiles.Count];
                _sharedFiles.Values.CopyTo(sharedFilesList, 0);
                return sharedFilesList;
            }
        }

        public void SendTypingNotification()
        {
            byte[] messageData = BitChatMessage.CreateTypingNotification();
            _network.WriteMessageBroadcast(messageData, 0, messageData.Length);
        }

        public MessageItem SendTextMessage(string message)
        {
            byte[] messageData = BitChatMessage.CreateTextMessage(message);
            _network.WriteMessageBroadcast(messageData, 0, messageData.Length);

            MessageItem msg = new MessageItem(_profile.LocalCertificateStore.Certificate.IssuedTo.EmailAddress.Address, message);
            msg.WriteTo(_store);

            return msg;
        }

        internal void WriteMessageBroadcast(byte[] data, int offset, int count)
        {
            _network.WriteMessageBroadcast(data, offset, count);
        }

        public void ShareFile(string filePath, string hashAlgo = "SHA1")
        {
            SharedFile sharedFile = SharedFile.ShareFile(filePath, hashAlgo, this, _syncCxt);

            lock (_sharedFiles)
            {
                if (!_sharedFiles.ContainsKey(sharedFile.MetaData.FileID))
                {
                    _sharedFiles.Add(sharedFile.MetaData.FileID, sharedFile);

                    if (FileAdded != null)
                        RaiseEventFileAdded(sharedFile);

                    //advertise file
                    SendFileAdvertisement(sharedFile);
                }
            }
        }

        public void LeaveChat()
        {
            //remove shared files
            lock (_sharedFiles)
            {
                List<SharedFile> _toRemove = new List<SharedFile>();

                foreach (KeyValuePair<BinaryID, SharedFile> sharedFile in _sharedFiles)
                    _toRemove.Add(sharedFile.Value);

                foreach (SharedFile sharedFile in _toRemove)
                    sharedFile.Remove(this);
            }

            //remove chat
            _manager.RemoveBitChat(this);

            //remove network
            _network.RemoveNetwork();

            //dispose
            this.Dispose();

            //delete message store index and data
            string messageStoreFolder = Path.Combine(_profile.ProfileFolder, "messages");

            try
            {
                File.Delete(Path.Combine(messageStoreFolder, _messageStoreID + ".index"));
            }
            catch
            { }

            try
            {
                File.Delete(Path.Combine(messageStoreFolder, _messageStoreID + ".data"));
            }
            catch
            { }

            if (Leave != null)
                RaiseEventLeave();
        }

        internal void RemoveSharedFile(SharedFile file)
        {
            lock (_sharedFiles)
            {
                _sharedFiles.Remove(file.MetaData.FileID);
            }
        }

        public MessageItem WriteInfoMessage(string info)
        {
            MessageItem msg = new MessageItem(info);
            msg.WriteTo(_store);

            return msg;
        }

        public MessageItem[] GetLastMessageItems(int index, int count)
        {
            return MessageItem.GetLastMessageItems(_store, index, count);
        }

        public int GetTotalMessageCount()
        {
            return _store.TotalMessages();
        }

        #endregion

        #region private

        private void network_VirtualPeerAdded(BitChatNetwork sender, BitChatNetwork.VirtualPeer virtualPeer)
        {
            Peer peer = new Peer(virtualPeer, this);

            lock (_peers)
            {
                _peers.Add(peer);
            }

            if (PeerAdded != null)
                RaiseEventPeerAdded(peer);
        }

        private void network_VirtualPeerHasRevokedCertificate(BitChatNetwork sender, InvalidCertificateException ex)
        {
            if (PeerHasRevokedCertificate != null)
                RaiseEventPeerHasRevokedCertificate(ex);
        }

        private void network_VirtualPeerSecureChannelException(BitChatNetwork sender, SecureChannelException ex)
        {
            if (PeerSecureChannelException != null)
                RaiseEventPeerSecureChannelException(ex);
        }

        private void network_VirtualPeerHasChangedCertificate(BitChatNetwork sender, Certificate cert)
        {
            if (PeerHasChangedCertificate != null)
                RaiseEventPeerHasChangedCertificate(cert);
        }

        private void SendFileAdvertisement(SharedFile sharedFile)
        {
            byte[] messageData = BitChatMessage.CreateFileAdvertisement(sharedFile.MetaData);
            _network.WriteMessageBroadcast(messageData, 0, messageData.Length);
        }

        private void profile_ProxyUpdated(object sender, EventArgs e)
        {
            _trackerManager.Proxy = _profile.Proxy;
        }

        #endregion

        #region PeerExchange implementation

        private void DoPeerExchange()
        {
            //find connected peers 
            List<PeerInfo> peerList = _network.GetConnectedPeerList();
            List<Peer> onlinePeers = new List<Peer>();

            lock (_peers)
            {
                foreach (Peer currentPeer in _peers)
                {
                    if (currentPeer.IsOnline)
                        onlinePeers.Add(currentPeer);
                }
            }

            //send other peers ep list to online peers
            byte[] messageData = BitChatMessage.CreatePeerExchange(peerList);

            foreach (Peer onlinePeer in onlinePeers)
            {
                onlinePeer.WriteMessage(messageData);
            }
        }

        private void TriggerUpdateNetworkStatus()
        {
            lock (_updateNetworkStatusLock)
            {
                if (!_updateNetworkStatusTriggered)
                {
                    _updateNetworkStatusTriggered = true;

                    if (!_updateNetworkStatusRunning)
                        _updateNetworkStatusTimer.Change(NETWORK_STATUS_TIMER_INTERVAL, Timeout.Infinite);
                }
            }
        }

        private void ReCheckNetworkStatusCallback(object state)
        {
            TriggerUpdateNetworkStatus();
        }

        private void UpdateNetworkStatusCallback(object state)
        {
            lock (_updateNetworkStatusLock)
            {
                if (_updateNetworkStatusRunning)
                    return;

                _updateNetworkStatusRunning = true;
                _updateNetworkStatusTriggered = false;
            }

            try
            {
                BitChatConnectivityStatus oldStatus = _connectivityStatus;
                BitChatConnectivityStatus connectivityStatus;

                if (_network.Status == BitChatNetworkStatus.Offline)
                {
                    connectivityStatus = BitChatConnectivityStatus.NoNetwork;

                    lock (_connectedPeerList)
                    {
                        _connectedPeerList.Clear();
                        _disconnectedPeerList.Clear();
                        _connectivityStatus = connectivityStatus;
                    }
                }
                else
                {
                    //find network wide connected peer ep list
                    List<PeerInfo> uniqueConnectedPeerList = new List<PeerInfo>();

                    List<Peer> onlinePeers = new List<Peer>();
                    List<Peer> offlinePeers = new List<Peer>();

                    lock (_peers)
                    {
                        foreach (Peer currentPeer in _peers)
                        {
                            if (currentPeer.IsOnline)
                                onlinePeers.Add(currentPeer);
                            else
                                offlinePeers.Add(currentPeer);
                        }
                    }

                    foreach (Peer onlinePeer in onlinePeers)
                    {
                        onlinePeer.UpdateUniqueConnectedPeerList(uniqueConnectedPeerList);
                    }

                    //find self connected & disconnected peer list
                    List<PeerInfo> connectedPeerList;
                    List<PeerInfo> disconnectedPeerList;

                    connectedPeerList = _network.GetConnectedPeerList();

                    //update self connected list
                    UpdateUniquePeerList(uniqueConnectedPeerList, connectedPeerList);

                    //remove self from unique connected peer list
                    PeerInfo selfPeerInfo = _network.GetSelfPeerInfo();
                    uniqueConnectedPeerList.Remove(selfPeerInfo);

                    //update connected peer's network status
                    foreach (Peer onlinePeer in onlinePeers)
                    {
                        onlinePeer.UpdateNetworkStatus(uniqueConnectedPeerList);
                    }

                    foreach (Peer offlinePeer in offlinePeers)
                    {
                        offlinePeer.SetNoNetworkStatus();
                    }

                    //find disconnected list
                    disconnectedPeerList = GetMissingPeerList(connectedPeerList, uniqueConnectedPeerList);

                    //update disconnected peer's network status
                    List<PeerInfo> dummyUniqueConnectedPeerList = new List<PeerInfo>(1);
                    dummyUniqueConnectedPeerList.Add(selfPeerInfo);

                    foreach (PeerInfo peerInfo in disconnectedPeerList)
                    {
                        //search all offline peers for comparison
                        foreach (Peer offlinePeer in offlinePeers)
                        {
                            if (offlinePeer.PeerCertificate.IssuedTo.EmailAddress.Address.Equals(peerInfo.PeerEmail))
                            {
                                offlinePeer.UpdateNetworkStatus(dummyUniqueConnectedPeerList);
                                break;
                            }
                        }
                    }

                    if (disconnectedPeerList.Count > 0)
                    {
                        connectivityStatus = BitChatConnectivityStatus.PartialNetwork;

                        foreach (PeerInfo peerInfo in disconnectedPeerList)
                            _network.MakeConnection(peerInfo.PeerEPList);
                    }
                    else
                    {
                        connectivityStatus = BitChatConnectivityStatus.FullNetwork;
                    }

                    lock (_connectedPeerList)
                    {
                        _connectedPeerList.Clear();
                        _connectedPeerList.AddRange(connectedPeerList);
                        _disconnectedPeerList = disconnectedPeerList;
                        _connectivityStatus = connectivityStatus;
                    }

                    if (_network.Type == BitChatNetworkType.PrivateChat)
                    {
                        if (connectedPeerList.Count > 0)
                        {
                            _manager.PauseLocalAnnouncement(_network.NetworkID);

                            if (_enableTracking)
                                _trackerManager.StopTracking();
                        }
                        else
                        {
                            _manager.ResumeLocalAnnouncement(_network.NetworkID);

                            if (_enableTracking)
                            {
                                _trackerManager.StartTracking();
                                _trackerManager.ForceUpdate();
                            }
                        }
                    }
                    else
                    {
                        if (connectedPeerList.Count > 0)
                        {
                            _manager.PauseLocalAnnouncement(_network.NetworkID);

                            if (_enableTracking)
                            {
                                if (disconnectedPeerList.Count > 0)
                                    _trackerManager.ForceUpdate();
                            }
                        }
                        else
                        {
                            _manager.ResumeLocalAnnouncement(_network.NetworkID);

                            if (_enableTracking)
                                _trackerManager.ForceUpdate();
                        }
                    }
                }

                if (oldStatus != connectivityStatus)
                    _selfPeer.RaiseEventNetworkStatusUpdated();
            }
            catch
            { }
            finally
            {
                lock (_updateNetworkStatusLock)
                {
                    if (_updateNetworkStatusTriggered)
                    {
                        _updateNetworkStatusTimer.Change(NETWORK_STATUS_TIMER_INTERVAL, Timeout.Infinite);
                    }
                    else
                    {
                        _updateNetworkStatusTriggered = false;

                        if (_connectivityStatus == BitChatConnectivityStatus.PartialNetwork)
                            _reCheckNetworkStatusTimer.Change(NETWORK_STATUS_RECHECK_TIMER_INTERVAL, Timeout.Infinite);
                    }

                    _updateNetworkStatusRunning = false;
                }
            }
        }

        private static void UpdateUniquePeerList(List<PeerInfo> uniquePeerList, List<PeerInfo> inputList)
        {
            foreach (PeerInfo item in inputList)
            {
                if (!uniquePeerList.Contains(item))
                    uniquePeerList.Add(item);
            }
        }

        private static List<PeerInfo> GetMissingPeerList(List<PeerInfo> mainList, List<PeerInfo> checkList)
        {
            List<PeerInfo> missingList = new List<PeerInfo>();

            foreach (PeerInfo checkEP in checkList)
            {
                if (!mainList.Contains(checkEP))
                    missingList.Add(checkEP);
            }

            return missingList;
        }

        #endregion

        #region NOOP implementation

        private void NOOPTimerCallback(object state)
        {
            try
            {
                byte[] messageData = BitChatMessage.CreateNOOPMessage();
                _network.WriteMessageBroadcast(messageData, 0, messageData.Length);
            }
            catch
            { }
            finally
            {
                if (_NOOPTimer != null)
                    _NOOPTimer.Change(NOOP_MESSAGE_TIMER_INTERVAL, Timeout.Infinite);
            }
        }

        #endregion

        #region TrackerManager

        private void trackerManager_DiscoveredPeers(TrackerManager sender, IEnumerable<IPEndPoint> peerEPs)
        {
            _network.MakeConnection(peerEPs);
        }

        public TrackerClient[] GetTrackers()
        {
            return _trackerManager.GetTrackers();
        }

        public int DhtGetTotalPeers()
        {
            return _trackerManager.DhtGetTotalPeers();
        }

        public IPEndPoint[] DhtGetPeers()
        {
            return _trackerManager.DhtGetPeers();
        }

        public void DhtUpdate()
        {
            _trackerManager.DhtUpdate();
        }

        public TimeSpan DhtNextUpdateIn()
        {
            return _trackerManager.DhtNextUpdateIn();
        }

        public Exception DhtLastException()
        {
            return _trackerManager.DhtLastException();
        }

        public TrackerClient AddTracker(Uri trackerURI)
        {
            return _trackerManager.AddTracker(trackerURI);
        }

        public void RemoveTracker(TrackerClient tracker)
        {
            _trackerManager.RemoveTracker(tracker);
        }

        public bool IsTrackerRunning
        { get { return _trackerManager.IsTrackerRunning; } }

        public bool EnableTracking
        {
            get
            {
                return _enableTracking;
            }
            set
            {
                _enableTracking = value;

                if (_network.Status != BitChatNetworkStatus.Offline)
                {
                    if (_network.Type == BitChatNetworkType.GroupChat)
                    {
                        if (_enableTracking)
                            _trackerManager.StartTracking();
                        else
                            _trackerManager.StopTracking();
                    }
                    else
                    {
                        if (_enableTracking)
                            TriggerUpdateNetworkStatus();
                        else
                            _trackerManager.StopTracking();
                    }
                }
            }
        }

        private void StopAllTracking()
        {
            _manager.StopLocalTracking(_network.NetworkID);
            _trackerManager.StopTracking();
        }

        private void StartAllTracking()
        {
            _manager.StartLocalTracking(_network.NetworkID);

            if (this.EnableTracking)
                this.EnableTracking = true;
        }

        #endregion

        #region properties

        public BinaryID NetworkID
        { get { return _network.NetworkID; } }

        public BitChatNetworkType NetworkType
        { get { return _network.Type; } }

        public BitChatNetworkStatus NetworkStatus
        {
            get { return _network.Status; }
            set
            {
                _network.Status = value;

                if (value == BitChatNetworkStatus.Offline)
                    StopAllTracking();
                else
                    StartAllTracking();

                TriggerUpdateNetworkStatus();
            }
        }

        public MailAddress PeerEmailAddress
        { get { return _network.PeerEmailAddress; } }

        public string NetworkName
        { get { return _network.NetworkName; } }

        public string SharedSecret
        { get { return _network.SharedSecret; } }

        public Certificate LocalCertificate
        { get { return _profile.LocalCertificateStore.Certificate; } }

        public Peer SelfPeer
        { get { return _selfPeer; } }

        #endregion

        public class Peer
        {
            #region events

            public event EventHandler StateChanged;
            public event EventHandler NetworkStatusUpdated;
            public event EventHandler ProfileImageChanged;

            #endregion

            #region variables

            BitChatNetwork.VirtualPeer _virtualPeer;
            BitChat _bitchat;

            byte[] _profileImageSmall;
            byte[] _profileImageLarge;

            List<PeerInfo> _connectedPeerList = new List<PeerInfo>();
            List<PeerInfo> _disconnectedPeerList = new List<PeerInfo>();
            BitChatConnectivityStatus _connectivityStatus = BitChatConnectivityStatus.NoNetwork;

            bool _isSelfPeer;
            bool _lastStatus = false;

            #endregion

            #region constructor

            internal Peer(BitChatNetwork.VirtualPeer virtualPeer, BitChat bitchat)
            {
                _virtualPeer = virtualPeer;
                _bitchat = bitchat;

                _isSelfPeer = (_virtualPeer.PeerCertificate.IssuedTo.EmailAddress.Address == _bitchat._profile.LocalCertificateStore.Certificate.IssuedTo.EmailAddress.Address);

                _virtualPeer.MessageReceived += virtualPeer_MessageReceived;
                _virtualPeer.StreamStateChanged += virtualPeer_StreamStateChanged;

                _bitchat._profile.ProfileImageChanged += profile_ProfileImageChanged;
            }

            #endregion

            #region private event functions

            private void RaiseEventStateChanged()
            {
                _bitchat._syncCxt.Post(StateChangedCallback, null);
            }

            private void StateChangedCallback(object state)
            {
                try
                {
                    StateChanged(this, EventArgs.Empty);
                }
                catch { }
            }

            internal void RaiseEventNetworkStatusUpdated()
            {
                _bitchat._syncCxt.Post(NetworkStatusUpdatedCallBack, null);
            }

            private void NetworkStatusUpdatedCallBack(object state)
            {
                try
                {
                    NetworkStatusUpdated(this, EventArgs.Empty);
                }
                catch { }
            }

            private void RaiseEventProfileImageChanged()
            {
                _bitchat._syncCxt.Post(ProfileImageChangedCallback, null);
            }

            private void ProfileImageChangedCallback(object state)
            {
                try
                {
                    ProfileImageChanged(this, EventArgs.Empty);
                }
                catch { }
            }

            #endregion

            #region private

            private void virtualPeer_StreamStateChanged(object sender, EventArgs args)
            {
                //trigger peer exchange for entire network
                _bitchat.DoPeerExchange();

                if (_virtualPeer.IsOnline)
                {
                    DoSendProfileImages();
                    DoSendSharedFileMetaData();
                }
                else
                {
                    lock (_bitchat._sharedFiles)
                    {
                        foreach (KeyValuePair<BinaryID, SharedFile> item in _bitchat._sharedFiles)
                        {
                            item.Value.RemovePeerOrSeeder(this);
                        }
                    }

                    lock (_connectedPeerList)
                    {
                        _connectedPeerList.Clear();
                        _disconnectedPeerList.Clear();
                    }

                    _bitchat.TriggerUpdateNetworkStatus();
                }

                if (!_isSelfPeer)
                {
                    if (_lastStatus != _virtualPeer.IsOnline)
                    {
                        _lastStatus = _virtualPeer.IsOnline;

                        if (StateChanged != null)
                            RaiseEventStateChanged();
                    }
                }
            }

            private void virtualPeer_MessageReceived(BitChatNetwork.VirtualPeer sender, Stream messageDataStream, IPEndPoint remotePeerEP)
            {
                BitChatMessageType type = BitChatMessage.ReadType(messageDataStream);

                switch (type)
                {
                    case BitChatMessageType.TypingNotification:
                        #region Typing Notification
                        {
                            if (_bitchat.PeerTyping != null)
                                _bitchat.RaiseEventPeerTyping(this);

                            break;
                        }
                        #endregion

                    case BitChatMessageType.Text:
                        #region Text
                        {
                            string message = Encoding.UTF8.GetString(BitChatMessage.ReadData(messageDataStream));

                            MessageItem msg = new MessageItem(_virtualPeer.PeerCertificate.IssuedTo.EmailAddress.Address, message);
                            msg.WriteTo(_bitchat._store);

                            if (_bitchat.MessageReceived != null)
                                _bitchat.RaiseEventMessageReceived(this, msg);

                            break;
                        }
                        #endregion

                    case BitChatMessageType.FileAdvertisement:
                        #region FileAdvertisement
                        {
                            SharedFile sharedFile = SharedFile.PrepareDownloadFile(BitChatMessage.ReadFileAdvertisement(messageDataStream), _bitchat, this, _bitchat._profile, _bitchat._syncCxt);

                            lock (_bitchat._sharedFiles)
                            {
                                if (_bitchat._sharedFiles.ContainsKey(sharedFile.MetaData.FileID))
                                {
                                    //file already exists
                                    if (sharedFile.IsComplete)
                                    {
                                        //remove the seeder
                                        sharedFile.RemovePeerOrSeeder(this);
                                    }
                                    else
                                    {
                                        sharedFile.AddChat(_bitchat);
                                        sharedFile.AddSeeder(this); //add the seeder

                                        WriteMessage(BitChatMessage.CreateFileParticipate(sharedFile.MetaData.FileID));
                                    }
                                }
                                else
                                {
                                    //file doesnt exists
                                    _bitchat._sharedFiles.Add(sharedFile.MetaData.FileID, sharedFile);

                                    if (_bitchat.FileAdded != null)
                                        _bitchat.RaiseEventFileAdded(sharedFile);
                                }
                            }

                            break;
                        }
                        #endregion

                    case BitChatMessageType.FileBlockRequest:
                        #region FileBlockRequest
                        {
                            FileBlockRequest blockRequest = BitChatMessage.ReadFileBlockRequest(messageDataStream);

                            ThreadPool.QueueUserWorkItem(ProcessFileSharingMessagesAsync, new object[] { type, blockRequest });
                            break;
                        }
                        #endregion

                    case BitChatMessageType.FileBlockResponse:
                        #region FileBlockResponse
                        {
                            FileBlockDataPart blockData = BitChatMessage.ReadFileBlockData(messageDataStream);

                            ThreadPool.QueueUserWorkItem(ProcessFileSharingMessagesAsync, new object[] { type, blockData });
                            break;
                        }
                        #endregion

                    case BitChatMessageType.FileBlockWanted:
                        #region FileBlockWanted
                        {
                            FileBlockWanted blockWanted = BitChatMessage.ReadFileBlockWanted(messageDataStream);

                            ThreadPool.QueueUserWorkItem(ProcessFileSharingMessagesAsync, new object[] { type, blockWanted });
                            break;
                        }
                        #endregion

                    case BitChatMessageType.FileBlockAvailable:
                        #region FileBlockAvailable
                        {
                            FileBlockWanted blockWanted = BitChatMessage.ReadFileBlockWanted(messageDataStream);

                            ThreadPool.QueueUserWorkItem(ProcessFileSharingMessagesAsync, new object[] { type, blockWanted });
                            break;
                        }
                        #endregion

                    case BitChatMessageType.FileShareParticipate:
                        #region FileShareParticipate
                        {
                            BinaryID fileID = BitChatMessage.ReadFileID(messageDataStream);

                            lock (_bitchat._sharedFiles)
                            {
                                _bitchat._sharedFiles[fileID].AddPeer(this);
                            }

                            break;
                        }
                        #endregion

                    case BitChatMessageType.FileShareUnparticipate:
                        #region FileShareUnparticipate
                        {
                            BinaryID fileID = BitChatMessage.ReadFileID(messageDataStream);

                            lock (_bitchat._sharedFiles)
                            {
                                _bitchat._sharedFiles[fileID].RemovePeerOrSeeder(this);
                            }

                            break;
                        }
                        #endregion

                    case BitChatMessageType.PeerExchange:
                        #region PeerExchange

                        List<PeerInfo> peerList = BitChatMessage.ReadPeerExchange(messageDataStream);

                        lock (_connectedPeerList)
                        {
                            //reason: for the lock to be valid for use
                            _connectedPeerList.Clear();
                            _connectedPeerList.AddRange(peerList);
                        }

                        foreach (PeerInfo peerInfo in peerList)
                        {
                            _bitchat._network.MakeConnection(peerInfo.PeerEPList);
                            _bitchat._dhtClient.AddNode(peerInfo.PeerEPList);
                        }

                        //start network status check
                        _bitchat.TriggerUpdateNetworkStatus();
                        break;

                        #endregion

                    case BitChatMessageType.ProfileImageSmall:
                        #region Profile Image Small
                        {
                            _profileImageSmall = BitChatMessage.ReadData(messageDataStream);

                            if (_profileImageSmall.Length == 0)
                                _profileImageSmall = null;

                            if (_isSelfPeer)
                                _bitchat._profile.ProfileImageSmall = _profileImageSmall;

                            RaiseEventProfileImageChanged();
                            break;
                        }
                        #endregion

                    case BitChatMessageType.ProfileImageLarge:
                        #region Profile Image Large
                        {
                            _profileImageLarge = BitChatMessage.ReadData(messageDataStream);

                            if (_profileImageLarge.Length == 0)
                                _profileImageLarge = null;

                            if (_isSelfPeer)
                                _bitchat._profile.ProfileImageLarge = _profileImageLarge;

                            break;
                        }
                        #endregion

                    case BitChatMessageType.NOOP:
                        break;
                }
            }

            private void profile_ProfileImageChanged(object sender, EventArgs e)
            {
                if (_isSelfPeer)
                    RaiseEventProfileImageChanged();

                DoSendProfileImages();
            }

            private void ProcessFileSharingMessagesAsync(object state)
            {
                object[] parameters = state as object[];

                BitChatMessageType type = (BitChatMessageType)parameters[0];

                try
                {
                    switch (type)
                    {
                        case BitChatMessageType.FileBlockRequest:
                            #region FileBlockRequest
                            {
                                FileBlockRequest blockRequest = parameters[1] as FileBlockRequest;
                                SharedFile sharedFile;

                                lock (_bitchat._sharedFiles)
                                {
                                    sharedFile = _bitchat._sharedFiles[blockRequest.FileID];
                                }

                                if (sharedFile.State == FileSharing.SharedFileState.Paused)
                                    return;

                                if (!sharedFile.PeerExists(this))
                                    return;

                                FileBlockDataPart blockData = sharedFile.ReadBlock(blockRequest.BlockNumber, blockRequest.BlockOffset, blockRequest.Length);

                                byte[] messageData = BitChatMessage.CreateFileBlockResponse(blockData);
                                _virtualPeer.WriteMessage(messageData, 0, messageData.Length);

                                break;
                            }
                            #endregion

                        case BitChatMessageType.FileBlockResponse:
                            #region FileBlockResponse
                            {
                                FileBlockDataPart blockData = parameters[1] as FileBlockDataPart;
                                SharedFile sharedFile;

                                lock (_bitchat._sharedFiles)
                                {
                                    sharedFile = _bitchat._sharedFiles[blockData.FileID];
                                }

                                if (sharedFile.State == FileSharing.SharedFileState.Paused)
                                    return;

                                if (!sharedFile.PeerExists(this))
                                    return;

                                SharedFile.FileBlockDownloadManager downloadingBlock = sharedFile.GetDownloadingBlock(blockData.BlockNumber);
                                if (downloadingBlock != null)
                                {
                                    if (downloadingBlock.IsThisDownloadPeerSet(this))
                                    {
                                        if (!downloadingBlock.SetBlockData(blockData))
                                        {
                                            byte[] messageData = BitChatMessage.CreateFileBlockRequest(downloadingBlock.GetNextRequest());
                                            _virtualPeer.WriteMessage(messageData, 0, messageData.Length);
                                        }
                                    }
                                }

                                break;
                            }
                            #endregion

                        case BitChatMessageType.FileBlockWanted:
                            #region FileBlockWanted
                            {
                                FileBlockWanted blockWanted = parameters[1] as FileBlockWanted;
                                SharedFile sharedFile;

                                lock (_bitchat._sharedFiles)
                                {
                                    sharedFile = _bitchat._sharedFiles[blockWanted.FileID];
                                }

                                if (sharedFile.State == FileSharing.SharedFileState.Paused)
                                    return;

                                if (!sharedFile.PeerExists(this))
                                    return;

                                if (sharedFile.IsBlockAvailable(blockWanted.BlockNumber))
                                {
                                    byte[] messageData = BitChatMessage.CreateFileBlockAvailable(blockWanted);
                                    _virtualPeer.WriteMessage(messageData, 0, messageData.Length);
                                }

                                break;
                            }
                            #endregion

                        case BitChatMessageType.FileBlockAvailable:
                            #region FileBlockAvailable
                            {
                                FileBlockWanted blockWanted = parameters[1] as FileBlockWanted;
                                SharedFile sharedFile;

                                lock (_bitchat._sharedFiles)
                                {
                                    sharedFile = _bitchat._sharedFiles[blockWanted.FileID];
                                }

                                if (sharedFile.IsComplete)
                                    return;

                                if (sharedFile.State == FileSharing.SharedFileState.Paused)
                                    return;

                                if (!sharedFile.PeerExists(this))
                                    return;

                                SharedFile.FileBlockDownloadManager downloadingBlock = sharedFile.GetDownloadingBlock(blockWanted.BlockNumber);
                                if (downloadingBlock != null)
                                {
                                    if (downloadingBlock.SetDownloadPeer(this))
                                    {
                                        byte[] messageData = BitChatMessage.CreateFileBlockRequest(downloadingBlock.GetNextRequest());
                                        _virtualPeer.WriteMessage(messageData, 0, messageData.Length);
                                    }
                                }

                                break;
                            }
                            #endregion
                    }
                }
                catch
                { }
            }

            private void DoSendProfileImages()
            {
                {
                    byte[] messageData = BitChatMessage.CreateProfileImageSmall(_bitchat._profile.ProfileImageSmall);
                    _virtualPeer.WriteMessage(messageData, 0, messageData.Length);
                }

                {
                    byte[] messageData = BitChatMessage.CreateProfileImageLarge(_bitchat._profile.ProfileImageLarge);
                    _virtualPeer.WriteMessage(messageData, 0, messageData.Length);
                }
            }

            private void DoSendSharedFileMetaData()
            {
                foreach (KeyValuePair<BinaryID, SharedFile> sharedFile in _bitchat._sharedFiles)
                {
                    if (sharedFile.Value.State == SharedFileState.Sharing)
                    {
                        byte[] messageData = BitChatMessage.CreateFileAdvertisement(sharedFile.Value.MetaData);
                        _virtualPeer.WriteMessage(messageData, 0, messageData.Length);
                    }
                }
            }

            #endregion

            #region public

            internal void UpdateUniqueConnectedPeerList(List<PeerInfo> uniqueConnectedPeerList)
            {
                lock (_connectedPeerList)
                {
                    UpdateUniquePeerList(uniqueConnectedPeerList, _connectedPeerList);
                }
            }

            internal void UpdateNetworkStatus(List<PeerInfo> uniqueConnectedPeerList)
            {
                BitChatConnectivityStatus oldStatus = _connectivityStatus;

                lock (_connectedPeerList)
                {
                    //compare this peer's connected peer list to the other peer list to find disconnected peer list
                    _disconnectedPeerList = GetMissingPeerList(_connectedPeerList, uniqueConnectedPeerList);
                    //remove self from the disconnected list
                    _disconnectedPeerList.Remove(_virtualPeer.GetPeerInfo());

                    if (_disconnectedPeerList.Count > 0)
                        _connectivityStatus = BitChatConnectivityStatus.PartialNetwork;
                    else
                        _connectivityStatus = BitChatConnectivityStatus.FullNetwork;
                }

                if (oldStatus != _connectivityStatus)
                    RaiseEventNetworkStatusUpdated();
            }

            internal void SetNoNetworkStatus()
            {
                BitChatConnectivityStatus oldStatus = _connectivityStatus;

                _connectivityStatus = BitChatConnectivityStatus.NoNetwork;

                if (oldStatus != _connectivityStatus)
                    RaiseEventNetworkStatusUpdated();
            }

            internal void WriteMessage(byte[] messageData)
            {
                _virtualPeer.WriteMessage(messageData, 0, messageData.Length);
            }

            public override string ToString()
            {
                if (_virtualPeer.PeerCertificate != null)
                    return _virtualPeer.PeerCertificate.IssuedTo.Name;

                return "unknown";
            }

            #endregion

            #region properties

            public PeerInfo[] ConnectedWith
            {
                get
                {
                    if (_isSelfPeer)
                    {
                        lock (_bitchat._connectedPeerList)
                        {
                            return _bitchat._connectedPeerList.ToArray();
                        }
                    }
                    else
                    {
                        lock (_connectedPeerList)
                        {
                            return _connectedPeerList.ToArray();
                        }
                    }
                }
            }

            public PeerInfo[] NotConnectedWith
            {
                get
                {
                    if (_isSelfPeer)
                    {
                        lock (_bitchat._connectedPeerList)
                        {
                            return _bitchat._disconnectedPeerList.ToArray();
                        }
                    }
                    else
                    {
                        lock (_connectedPeerList)
                        {
                            return _disconnectedPeerList.ToArray();
                        }
                    }
                }
            }

            public BitChatConnectivityStatus ConnectivityStatus
            {
                get
                {
                    if (_isSelfPeer)
                        return _bitchat._connectivityStatus;
                    else
                        return _connectivityStatus;
                }
            }

            public SecureChannelCryptoOptionFlags CipherSuite
            { get { return _virtualPeer.CipherSuite; } }

            public Certificate PeerCertificate
            {
                get
                {
                    if (_isSelfPeer)
                        return _bitchat._profile.LocalCertificateStore.Certificate;
                    else
                        return _virtualPeer.PeerCertificate;
                }
            }

            public byte[] ProfileImageSmall
            {
                get
                {
                    if (_isSelfPeer)
                        return _bitchat._profile.ProfileImageSmall;
                    else
                        return _profileImageSmall;
                }
            }

            public byte[] ProfileImageLarge
            {
                get
                {
                    if (_isSelfPeer)
                        return _bitchat._profile.ProfileImageLarge;
                    else
                        return _profileImageLarge;
                }
            }

            public bool IsOnline
            {
                get
                {
                    if (_isSelfPeer)
                        return true;
                    else
                        return _virtualPeer.IsOnline;
                }
            }

            public bool IsSelf
            { get { return _isSelfPeer; } }

            #endregion
        }
    }
}

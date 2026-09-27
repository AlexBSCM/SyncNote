package com.syncnote

import android.annotation.SuppressLint
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.net.NetworkInfo
import android.net.wifi.p2p.WifiP2pConfig
import android.net.wifi.p2p.WifiP2pDevice
import android.net.wifi.p2p.WifiP2pInfo
import android.net.wifi.p2p.WifiP2pManager
import android.os.Build

// Wi-Fi Direct соединение с ПК.
// NOT-VERIFIED: needs hardware — эмулятор P2P не поддерживает,
// проверка только на физическом телефоне + ПК с GO-группой.
// Использование: discover(nameHint) -> connect(device) -> адрес GO в колбэке.
class P2pConnector(private val ctx: Context) {
    interface Listener {
        fun onState(text: String)
        fun onPeers(devices: List<WifiP2pDevice>)
        fun onGroupOwner(address: String)
        fun onError(reason: String)
    }

    private val manager: WifiP2pManager? =
        ctx.getSystemService(Context.WIFI_P2P_SERVICE) as? WifiP2pManager
    private var channel: WifiP2pManager.Channel? = null
    var listener: Listener? = null

    private val receiver = object : BroadcastReceiver() {
        override fun onReceive(c: Context, intent: Intent) {
            when (intent.action) {
                WifiP2pManager.WIFI_P2P_STATE_CHANGED_ACTION -> {
                    val state = intent.getIntExtra(WifiP2pManager.EXTRA_WIFI_STATE, -1)
                    listener?.onState(if (state == WifiP2pManager.WIFI_P2P_STATE_ENABLED)
                        "P2P включён" else "P2P выключен")
                }
                WifiP2pManager.WIFI_P2P_PEERS_CHANGED_ACTION ->
                    manager?.requestPeers(channel) { list ->
                        listener?.onPeers(list.deviceList.toList())
                    }
                WifiP2pManager.WIFI_P2P_CONNECTION_CHANGED_ACTION -> {
                    val info: NetworkInfo? =
                        intent.getParcelableExtra(WifiP2pManager.EXTRA_NETWORK_INFO)
                    if (info?.isConnected == true) {
                        manager?.requestConnectionInfo(channel) { ci: WifiP2pInfo ->
                            ci.groupOwnerAddress?.hostAddress?.let {
                                listener?.onGroupOwner(it)
                            } ?: listener?.onError("Нет адреса GO.")
                        }
                    }
                }
            }
        }
    }

    fun start() {
        if (manager == null) {
            listener?.onError("Устройство без Wi-Fi Direct.")
            return
        }
        channel = manager.initialize(ctx, ctx.mainLooper, null)
        val filter = IntentFilter().apply {
            addAction(WifiP2pManager.WIFI_P2P_STATE_CHANGED_ACTION)
            addAction(WifiP2pManager.WIFI_P2P_PEERS_CHANGED_ACTION)
            addAction(WifiP2pManager.WIFI_P2P_CONNECTION_CHANGED_ACTION)
        }
        if (Build.VERSION.SDK_INT >= 33)
            ctx.registerReceiver(receiver, filter, Context.RECEIVER_NOT_EXPORTED)
        else
            @Suppress("UnspecifiedRegisterReceiverFlag")
            ctx.registerReceiver(receiver, filter)
    }

    fun stop() {
        try { ctx.unregisterReceiver(receiver) } catch (_: Exception) { }
    }

    @SuppressLint("MissingPermission")
    fun discover() {
        listener?.onState("Ищем устройство…")
        manager?.discoverPeers(channel, object : WifiP2pManager.ActionListener {
            override fun onSuccess() {}
            override fun onFailure(reason: Int) {
                listener?.onError("Поиск не запустился: $reason")
            }
        }) ?: listener?.onError("Нет P2P-менеджера.")
    }

    @SuppressLint("MissingPermission")
    fun connect(device: WifiP2pDevice) {
        listener?.onState("Подключаемся к ${device.deviceName}…")
        val cfg = WifiP2pConfig().apply {
            deviceAddress = device.deviceAddress
        }
        manager?.connect(channel, cfg, object : WifiP2pManager.ActionListener {
            override fun onSuccess() {
                listener?.onState("Ожидаем подтверждение…")
            }
            override fun onFailure(reason: Int) {
                listener?.onError("Подключение не удалось: $reason")
            }
        })
    }
}

package com.syncnote

import android.annotation.SuppressLint
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothSocket
import android.content.Context
import java.io.DataInputStream
import java.io.DataOutputStream
import java.util.UUID

// Bluetooth RFCOMM-резерв (только после спаривания в настройках ОС).
// Тот же протокол поверх сокета. UUID общий с Windows (см. protocol/).
// NOT-VERIFIED: needs hardware — проверка на двух устройствах (T7).
object BluetoothConnector {
    val SERVICE_UUID: UUID =
        UUID.fromString("8f6d4b2a-1c3e-4a5f-9b6d-7e8f9a0b1c2d")

    fun adapter(ctx: Context): BluetoothAdapter? =
        (ctx.getSystemService(Context.BLUETOOTH_SERVICE) as? BluetoothManager)?.adapter

    fun bonded(ctx: Context): List<BluetoothDevice> =
        try {
            adapter(ctx)?.bondedDevices?.toList() ?: emptyList()
        } catch (_: SecurityException) {
            emptyList()
        }

    @SuppressLint("MissingPermission")
    @Throws(java.io.IOException::class)
    fun connect(device: BluetoothDevice): Pair<DataInputStream, DataOutputStream> {
        val sock: BluetoothSocket =
            device.createRfcommSocketToServiceRecord(SERVICE_UUID)
        try {
            sock.connect()
        } catch (e: java.io.IOException) {
            try { sock.close() } catch (_: Exception) { }
            throw e
        }
        // Закрытие потоков закроет сокет (владелец — вызывающий).
        return DataInputStream(sock.inputStream) to DataOutputStream(sock.outputStream)
    }
}

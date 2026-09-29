package com.syncnote.v2.domain

import org.json.JSONObject

// Пейлоад QR сопряжения: только нужное для соединения.
// Парсинг строгий: мусор отклоняется до любых сетевых действий.
data class QrPayload(val ip: String, val port: Int, val token: String) {
    companion object {
        fun parse(json: String): QrPayload? {
            val o = try {
                JSONObject(json)
            } catch (_: Exception) {
                return null
            }
            val ip = o.optString("ip", "").trim()
            val port = o.optInt("port", 0)
            val token = o.optString("token", "")
            if (ip.isEmpty() || port !in 1..65535 || token.isEmpty())
                return null
            // Токен в логи не пишем никогда — даже его длину светим только здесь,
            // в тесте, но не в продовых путях.
            return QrPayload(ip, port, token)
        }
    }
}

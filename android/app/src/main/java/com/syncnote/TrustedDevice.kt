package com.syncnote

// Доверенное устройство (ПК), с которым телефон уже сопрягался.
// Бэклог 1.c. id стабильный и выводим из host:port, чтобы addOrUpdate
// был идемпотентным, а переименование не меняло идентичность записи.
data class TrustedDevice(
    val id: String,
    val host: String,
    val port: Int,
    val name: String,
    val lastUsed: Long
) {
    companion object {
        fun idFor(host: String, port: Int): String = "$host:$port"
    }
}

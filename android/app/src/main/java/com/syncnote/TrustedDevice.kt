package com.syncnote

// Доверенное устройство (ПК), с которым телефон уже сопрягался.
// Бэклог 1.c. id стабильный и выводим из host:port, чтобы addOrUpdate
// был идемпотентным, а переименование не меняло идентичность записи.
//
// token — одноразовый токен первого сопряжения из QR. Сервер отклоняет
// сессию без него («нет доверия: нужен токен»), а после первого успеха
// устройство считается доверенным и токен больше не нужен, поэтому мы
// его стираем (то же поведение, что в SyncActivity.runSync).
data class TrustedDevice(
    val id: String,
    val host: String,
    val port: Int,
    val name: String,
    val lastUsed: Long,
    val token: String? = null
) {
    companion object {
        fun idFor(host: String, port: Int): String = "$host:$port"
    }
}

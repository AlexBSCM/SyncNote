package com.syncnote.v2.network

// Отказ в рукопожатии: чужой токен. Отдельный тип, чтобы UI отличал
// «не пустили» от обрыва сети. Значений токенов в сообщениях нет.
class HelloRejectedException(reason: String) : java.io.IOException(reason)

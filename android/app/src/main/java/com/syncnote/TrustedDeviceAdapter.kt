package com.syncnote

import android.content.res.ColorStateList
import android.view.LayoutInflater
import android.view.ViewGroup
import androidx.core.content.ContextCompat
import androidx.recyclerview.widget.RecyclerView
import com.syncnote.databinding.ItemPeerRowBinding

// Список доверенных ПК на Smart Start Screen (бэклог 1.c).
// Использует ту же карточку item_peer_row, что и список Wi-Fi Direct пиров.
// Долгий тап — переименование, короткий — подключение.
class TrustedDeviceAdapter(
    private val onClick: (TrustedDevice) -> Unit,
    private val onLongClick: (TrustedDevice) -> Unit
) : RecyclerView.Adapter<TrustedDeviceAdapter.Holder>() {

    private var items: List<TrustedDevice> = emptyList()
    private var connectingId: String? = null

    fun submit(list: List<TrustedDevice>) {
        items = list
        notifyDataSetChanged()
    }

    // Id устройства, которое сейчас подключается (для блокировки повторов).
    fun setConnecting(id: String?) {
        if (connectingId == id) return
        connectingId = id
        notifyDataSetChanged()
    }

    class Holder(val b: ItemPeerRowBinding) : RecyclerView.ViewHolder(b.root)

    override fun onCreateViewHolder(p: ViewGroup, v: Int) = Holder(
        ItemPeerRowBinding.inflate(LayoutInflater.from(p.context), p, false))

    override fun getItemCount() = items.size

    override fun onBindViewHolder(h: Holder, pos: Int) {
        val d = items[pos]
        val busy = connectingId == d.id
        h.b.peerName.text = d.name
        h.b.peerStatus.text = "${d.host}:${d.port}"

        // Карточка снимается с нажатий, пока идёт подключение.
        h.itemView.isEnabled = !busy && connectingId == null
        h.b.peerIcon.alpha = if (busy) 0.5f else 1f

        h.b.peerLed.backgroundTintList = ColorStateList.valueOf(
            ContextCompat.getColor(h.itemView.context, R.color.text_hint))
        h.b.peerLed.visibility = if (busy) android.view.View.GONE
        else android.view.View.VISIBLE
        h.b.peerProgress.visibility =
            if (busy) android.view.View.VISIBLE else android.view.View.GONE

        h.itemView.setOnClickListener { if (h.itemView.isEnabled) onClick(d) }
        h.itemView.setOnLongClickListener { onLongClick(d); true }
    }
}

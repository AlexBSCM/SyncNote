package com.syncnote

import android.content.Context
import android.graphics.drawable.GradientDrawable
import android.view.LayoutInflater
import android.view.ViewGroup
import androidx.core.content.ContextCompat
import androidx.recyclerview.widget.DiffUtil
import androidx.recyclerview.widget.ListAdapter
import androidx.recyclerview.widget.RecyclerView
import com.syncnote.databinding.ItemFileRowBinding

// Скруглённые подложки из цветов ресурсов (hex только в colors.xml).
// Общие для адаптера списка и bottom sheet — без новых файлов/модулей.
internal fun roundedBox(ctx: Context, color: Int, radiusDp: Float): GradientDrawable =
    GradientDrawable().apply {
        shape = GradientDrawable.RECTANGLE
        setColor(color)
        cornerRadius = radiusDp * ctx.resources.displayMetrics.density
    }

// Строка списка файлов: иконка + название/мета + бейдж статуса.
class FilesAdapter(private val onItemClick: (String) -> Unit) :
    ListAdapter<FileUiModel, FilesAdapter.Holder>(DIFF) {

    companion object {
        private val DIFF = object : DiffUtil.ItemCallback<FileUiModel>() {
            override fun areItemsTheSame(a: FileUiModel, b: FileUiModel) = a.id == b.id
            override fun areContentsTheSame(a: FileUiModel, b: FileUiModel) = a == b
        }
    }

    class Holder(val b: ItemFileRowBinding) : RecyclerView.ViewHolder(b.root)

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): Holder {
        val b = ItemFileRowBinding.inflate(
            LayoutInflater.from(parent.context), parent, false)
        return Holder(b)
    }

    override fun onBindViewHolder(h: Holder, pos: Int) {
        val m = getItem(pos)
        val ctx = h.itemView.context
        h.b.fileName.text = m.name
        h.b.fileMeta.text = m.metaLine
        h.b.fileIcon.text = m.iconEmoji
        h.b.fileIcon.background = roundedBox(ctx,
            ContextCompat.getColor(ctx, m.iconBgRes), 8f)
        h.b.fileIcon.setTextColor(ContextCompat.getColor(ctx, m.iconFgRes))
        h.b.fileBadge.text = ctx.getString(m.badge.textRes)
        h.b.fileBadge.background = roundedBox(ctx,
            ContextCompat.getColor(ctx, m.badge.bgRes), 10f)
        h.b.fileBadge.setTextColor(ContextCompat.getColor(ctx, m.badge.fgRes))
        h.itemView.setOnClickListener { onItemClick(m.id) }
    }
}

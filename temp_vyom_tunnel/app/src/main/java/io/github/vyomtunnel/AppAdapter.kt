package io.github.vyomtunnel

import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.ImageView
import android.widget.TextView
import androidx.appcompat.widget.SwitchCompat
import androidx.recyclerview.widget.RecyclerView
import io.github.vyomtunnel.sdk.utils.VyomAppInfo

class AppAdapter(
    private var allApps: List<VyomAppInfo>,
    private val onToggle: (String) -> Unit
) : RecyclerView.Adapter<AppAdapter.AppViewHolder>() {

    private var displayedApps = allApps

    class AppViewHolder(view: View) : RecyclerView.ViewHolder(view) {
        val icon: ImageView = view.findViewById(R.id.ivAppIcon)
        val name: TextView = view.findViewById(R.id.tvAppName)
        val pkg: TextView = view.findViewById(R.id.tvPackageName)
        val sw: SwitchCompat = view.findViewById(R.id.swExclude)
    }

    fun filter(query: String) {
        displayedApps = if (query.isEmpty()) {
            allApps
        } else {
            allApps.filter { it.name.contains(query, ignoreCase = true) || it.packageName.contains(query, ignoreCase = true) }
        }
        notifyDataSetChanged()
    }

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): AppViewHolder {
        val view = LayoutInflater.from(parent.context).inflate(R.layout.item_app, parent, false)
        return AppViewHolder(view)
    }

    override fun onBindViewHolder(holder: AppViewHolder, position: Int) {
        val app = displayedApps[position]
        holder.name.text = app.name
        holder.pkg.text = app.packageName
        holder.icon.setImageDrawable(app.icon)
        holder.sw.isChecked = app.isExcluded

        holder.itemView.setOnClickListener {
            onToggle(app.packageName)
            val updatedApp = app.copy(isExcluded = !app.isExcluded)
            allApps = allApps.map { if (it.packageName == app.packageName) updatedApp else it }
            displayedApps = displayedApps.map { if (it.packageName == app.packageName) updatedApp else it }
            notifyItemChanged(position)
        }
    }

    override fun getItemCount() = displayedApps.size
}
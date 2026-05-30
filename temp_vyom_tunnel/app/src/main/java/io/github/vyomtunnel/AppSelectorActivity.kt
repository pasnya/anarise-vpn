package io.github.vyomtunnel

import android.os.Bundle
import android.text.Editable
import android.text.TextWatcher
import android.widget.EditText
import androidx.appcompat.app.AppCompatActivity
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import io.github.vyomtunnel.sdk.VyomVpnManager
import io.github.vyomtunnel.sdk.utils.AppInventory
import kotlin.concurrent.thread

class AppSelectorActivity : AppCompatActivity() {

    private lateinit var adapter: AppAdapter

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_app_selector)
        supportActionBar?.title = "Split Tunneling"

        val rvApps = findViewById<RecyclerView>(R.id.rvApps)
        val etSearch = findViewById<EditText>(R.id.etSearch)

        rvApps.layoutManager = LinearLayoutManager(this)

        // Load apps in background
        thread {
            val apps = AppInventory.getInstalledApps(this)
            runOnUiThread {
                adapter = AppAdapter(apps) { packageName ->
                    VyomVpnManager.toggleAppExclusion(this, packageName)
                }
                rvApps.adapter = adapter
            }
        }

        etSearch.addTextChangedListener(object : TextWatcher {
            override fun afterTextChanged(s: Editable?) { adapter.filter(s.toString()) }
            override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) {}
            override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) {}
        })
    }
}
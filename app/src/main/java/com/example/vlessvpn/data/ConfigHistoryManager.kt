package com.example.vlessvpn.data

import android.content.Context
import org.json.JSONArray

object ConfigHistoryManager {
    private const val PREFS_NAME = "vless_history_prefs"
    private const val KEY_HISTORY = "config_history"
    private const val MAX_HISTORY_SIZE = 10

    fun saveConfigToHistory(context: Context, link: String) {
        val trimmed = link.trim()
        if (trimmed.isBlank()) return
        val currentHistory = getHistory(context).toMutableList()
        currentHistory.remove(trimmed) // Remove existing to avoid duplicates and reorder
        currentHistory.add(0, trimmed) // Insert at top
        if (currentHistory.size > MAX_HISTORY_SIZE) {
            currentHistory.removeAt(currentHistory.lastIndex)
        }
        saveHistory(context, currentHistory)
    }

    fun getHistory(context: Context): List<String> {
        val prefs = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
        val jsonStr = prefs.getString(KEY_HISTORY, null) ?: return emptyList()
        return try {
            val jsonArray = JSONArray(jsonStr)
            val list = mutableListOf<String>()
            for (i in 0 until jsonArray.length()) {
                list.add(jsonArray.getString(i))
            }
            list
        } catch (e: Exception) {
            emptyList()
        }
    }

    fun deleteFromHistory(context: Context, link: String) {
        val trimmed = link.trim()
        val currentHistory = getHistory(context).toMutableList()
        currentHistory.remove(trimmed)
        saveHistory(context, currentHistory)
    }

    private fun saveHistory(context: Context, history: List<String>) {
        val jsonArray = JSONArray()
        history.forEach { jsonArray.put(it) }
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
            .edit()
            .putString(KEY_HISTORY, jsonArray.toString())
            .apply()
    }
}

<script setup lang="ts">
import { ref } from 'vue'

import { fromSerializedDarkModel, type Model } from './types'
import Header from './components/Header.vue'
// import ConversationView from './views/ConversationView.vue'
import MainView from './views/MainView.vue'

let init: Model = {
  isLoading: false,
  systemPrompt: '<system prompt here>!',
  chatHistory: [],
  codeSnippets: [],
  tasks: [],
  actions: [],
  functions: [],
  types: [],
}

// Set initial state; listen for state updates from Dark
const state = ref(init)
window.stateUpdated = (serializedNewState: string) => {
  //console.log('stateUpdated', serializedNewState)
  try {
    const parsed = fromSerializedDarkModel(serializedNewState)
    state.value = parsed
  } catch (e) {
    console.error('Failed to parse updated state', e)
  }
}

// Bootstrap and connect the Dark side of the app
// (running in WebAssembly)
const darklangJSScript: HTMLScriptElement = document.createElement('script')
darklangJSScript.setAttribute(
  'src',
  'http://dark-serve-static.dlio.localhost:11003/editor-bootstrap.js'
)
darklangJSScript.setAttribute('defer', '')
darklangJSScript.addEventListener('load', async () => {
  // TODO: maybe do this on `onMounted`?
  const darklang = await window.Darklang.init('dark-editor', window.stateUpdated)

  // TODO: we don't need to expose this onace the logic in ResponseChat.vue is
  // ported to Dark.
  window.darklang = darklang

  window.darklang.handleEvent({ LoadSystemPrompt: [] })
})

document.head.appendChild(darklangJSScript)
</script>

<template>
  <div class="h-screen overflow-hidden">
    <Header />
    <!-- <ConversationView v-bind:state="state" /> -->
    <MainView v-bind:state="state" />
  </div>
</template>

import { onPage } from '../Navigation/PageSwap';
import { initializeSpeechSettings } from './Preferences';

onPage(import.meta.url, () => initializeSpeechSettings(document.documentElement));

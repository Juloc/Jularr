import { onPage } from '../../Modules/Navigation/PageSwap';
import { initializeTimePickers } from './TimePicker';

onPage(import.meta.url, () => initializeTimePickers(), { partial: true });

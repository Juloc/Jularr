import { onPage } from '../../Modules/Navigation/PageSwap';
import { initializePreview } from './MediaPreview';

onPage(import.meta.url, initializePreview);

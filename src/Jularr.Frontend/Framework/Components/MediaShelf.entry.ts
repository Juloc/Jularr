import { onPage } from '../../Modules/Navigation/PageSwap';
import { initializeShelves } from './MediaShelf';

onPage(import.meta.url, () => initializeShelves());

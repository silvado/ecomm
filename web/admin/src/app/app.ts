import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { FeedbackHost } from './core/feedback';

@Component({
  imports: [RouterOutlet, FeedbackHost],
  selector: 'app-root',
  template: '<router-outlet /><app-feedback />',
})
export class App {}
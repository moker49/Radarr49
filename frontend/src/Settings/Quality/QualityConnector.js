import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import * as commandNames from 'Commands/commandNames';
import { fetchQualityProfiles } from 'Store/Actions/settingsActions';
import createCommandExecutingSelector from 'Store/Selectors/createCommandExecutingSelector';
import Quality from './Quality';

function createMapStateToProps() {
  return createSelector(
    createCommandExecutingSelector(commandNames.RESET_QUALITY_DEFINITIONS),
    (state) => state.settings.qualityProfiles,
    (isResettingQualityDefinitions, qualityProfiles) => {
      return {
        isResettingQualityDefinitions,
        profiles: qualityProfiles.items.filter((profile) => !profile.isComposite),
        isFetching: qualityProfiles.isFetching,
        isPopulated: qualityProfiles.isPopulated,
        error: qualityProfiles.error
      };
    }
  );
}

class QualityConnector extends Component {

  componentDidMount() {
    this.props.fetchQualityProfiles();
  }

  //
  // Render

  render() {
    return (
      <Quality
        {...this.props}
      />
    );
  }
}

QualityConnector.propTypes = {
  fetchQualityProfiles: PropTypes.func.isRequired,
  isResettingQualityDefinitions: PropTypes.bool.isRequired
};

export default connect(createMapStateToProps, { fetchQualityProfiles })(QualityConnector);

import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import * as commandNames from 'Commands/commandNames';
import { fetchQualityDefinitions, fetchQualityProfiles } from 'Store/Actions/settingsActions';
import createCommandExecutingSelector from 'Store/Selectors/createCommandExecutingSelector';
import Quality from './Quality';

function createMapStateToProps() {
  return createSelector(
    createCommandExecutingSelector(commandNames.RESET_QUALITY_DEFINITIONS),
    (state) => state.settings.qualityProfiles,
    (state) => state.settings.qualityDefinitions,
    (isResettingQualityDefinitions, qualityProfiles, qualityDefinitions) => {
      return {
        isResettingQualityDefinitions,
        profiles: qualityProfiles.items.filter((profile) => !profile.isComposite),
        isFetching: qualityProfiles.isFetching || qualityDefinitions.isFetching,
        isPopulated: qualityProfiles.isPopulated && qualityDefinitions.isPopulated,
        error: qualityProfiles.error || qualityDefinitions.error
      };
    }
  );
}

class QualityConnector extends Component {

  componentDidMount() {
    this.props.fetchQualityProfiles();
    this.props.fetchQualityDefinitions();
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
  fetchQualityDefinitions: PropTypes.func.isRequired,
  isResettingQualityDefinitions: PropTypes.bool.isRequired
};

export default connect(createMapStateToProps, { fetchQualityProfiles, fetchQualityDefinitions })(QualityConnector);
